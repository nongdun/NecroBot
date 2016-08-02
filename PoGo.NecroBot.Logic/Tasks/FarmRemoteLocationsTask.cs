#region using directives

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Map.Fort;
using POGOProtos.Networking.Responses;
using POGOProtos.Map.Pokemon;
using POGOProtos.Enums;
using POGOProtos.Inventory.Item;
using PoGo.NecroBot.Logic.PoGoUtils;
using PoGo.NecroBot.Logic.Logging;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class FarmRemoteLocationsTask
    {
        private static DateTime _lastTasksCall = DateTime.Now;

        public static async Task Execute(ISession session, CancellationToken cancellationToken)
        {
            var tracks = GetGpxTracks(session);
            var eggWalker = new EggWalker(1000, session);

            for (var curTrk = 0; curTrk < tracks.Count; curTrk++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var track = tracks.ElementAt(curTrk);
                var trackSegments = track.Segments;
                for (var curTrkSeg = 0; curTrkSeg < trackSegments.Count; curTrkSeg++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var trackPoints = track.Segments.ElementAt(0).TrackPoints;
                    for (var curTrkPt = 0; curTrkPt < trackPoints.Count; curTrkPt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var nextPoint = trackPoints.ElementAt(curTrkPt);


                        var pokemonIds = session.LogicSettings.PokemonToSnipe.Pokemon;

                        if (!await CheckPokeballsToSnipe(session.LogicSettings.MinPokeballsToSnipe + 1, session, cancellationToken))
                        {
                            Logger.Write("Ball not enough, go and get balls...", LogLevel.Warning);
                            await FarmPokestopsGetBallsTask.Execute(session, cancellationToken);
                        }

                        if (session.LogicSettings.SnipeAtPokestops || session.LogicSettings.UseSnipeLocationServer)
                        {
                            await SnipePokemonTask.Execute(session, cancellationToken);
                        }

                        await Snipe(session, pokemonIds, Convert.ToDouble(nextPoint.Lat), Convert.ToDouble(nextPoint.Lon), cancellationToken);

                        if (DateTime.Now > _lastTasksCall)
                        {
                            _lastTasksCall = DateTime.Now.AddMilliseconds(60000);   //每60秒清一次

                            await RecycleItemsTask.Execute(session, cancellationToken);

                            if (session.LogicSettings.SnipeAtPokestops || session.LogicSettings.UseSnipeLocationServer)
                            {
                                await SnipePokemonTask.Execute(session, cancellationToken);
                            }

                            if (session.LogicSettings.EvolveAllPokemonWithEnoughCandy ||
                                session.LogicSettings.EvolveAllPokemonAboveIv)
                            {
                                await EvolvePokemonTask.Execute(session, cancellationToken);
                            }

                            if (session.LogicSettings.TransferDuplicatePokemon)
                            {
                                await TransferDuplicatePokemonTask.Execute(session, cancellationToken);
                            }

                            if (session.LogicSettings.RenamePokemon)
                            {
                                await RenamePokemonTask.Execute(session, cancellationToken);
                            }

                            if (session.LogicSettings.AutoFavoritePokemon)
                            {
                                await FavoritePokemonTask.Execute(session, cancellationToken);
                            }
                        }

                        //await eggWalker.ApplyDistance(distance, cancellationToken);
                    } //end trkpts
                } //end trksegs
            } //end tracks
        }

        private static List<GpxReader.Trk> GetGpxTracks(ISession session)
        {
            var xmlString = File.ReadAllText(session.LogicSettings.GpxFile);
            var readgpx = new GpxReader(xmlString, session);
            return readgpx.Tracks;
        }

        //Please do not change GetPokeStops() in this file, it's specifically set
        //to only find stops within 40 meters
        //this is for gpx pathing, we are not going to the pokestops,
        //so do not make it more than 40 because it will never get close to those stops.
        private static async Task<List<FortData>> GetPokeStops(ISession session)
        {
            var mapObjects = await session.Client.Map.GetMapObjects();

            // Wasn't sure how to make this pretty. Edit as needed.
            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts)
                .Where(
                    i =>
                        i.Type == FortType.Checkpoint &&
                        i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime() &&
                        ( // Make sure PokeStop is within 40 meters or else it is pointless to hit it
                            LocationUtils.CalculateDistanceInMeters(
                                session.Client.CurrentLatitude, session.Client.CurrentLongitude,
                                i.Latitude, i.Longitude) < 40) ||
                        session.LogicSettings.MaxTravelDistanceInMeters == 0
                );

            return pokeStops.ToList();
        }


        private static async Task Snipe(ISession session, IEnumerable<PokemonId> pokemonIds, double latitude,
            double longitude, CancellationToken cancellationToken)
        {
            var CurrentLatitude = session.Client.CurrentLatitude;
            var CurrentLongitude = session.Client.CurrentLongitude;

            session.EventDispatcher.Send(new SnipeModeEvent { Active = true });

            Logger.Write($"Catch pokemon on location: [{latitude.ToString()}, {longitude.ToString()}]", LogLevel.Info);
            List<MapPokemon> catchablePokemon;
            try
            {
                await
                    session.Client.Player.UpdatePlayerLocation(latitude, longitude, session.Client.CurrentAltitude);

                session.EventDispatcher.Send(new UpdatePositionEvent
                {
                    Longitude = longitude,
                    Latitude = latitude
                });

                var mapObjects = session.Client.Map.GetMapObjects().Result;
                catchablePokemon =
                    mapObjects.MapCells.SelectMany(q => q.CatchablePokemons)
                        .Where(q => pokemonIds.Contains(q.PokemonId))
                        .OrderByDescending(pokemon => PokemonInfo.CalculateMaxCpMultiplier(pokemon.PokemonId))
                        .ToList();
            }
            finally
            {
                await
                    session.Client.Player.UpdatePlayerLocation(CurrentLatitude, CurrentLongitude, session.Client.CurrentAltitude);
            }

            foreach (var pokemon in catchablePokemon)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EncounterResponse encounter;
                try
                {
                    await
                        session.Client.Player.UpdatePlayerLocation(latitude, longitude, session.Client.CurrentAltitude);

                    encounter =
                        session.Client.Encounter.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnPointId).Result;
                }
                finally
                {
                    await
                        session.Client.Player.UpdatePlayerLocation(CurrentLatitude, CurrentLongitude,
                            session.Client.CurrentAltitude);
                }

                if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                {
                    session.EventDispatcher.Send(new UpdatePositionEvent
                    {
                        Latitude = CurrentLatitude,
                        Longitude = CurrentLongitude
                    });

                    await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon);
                }
                else if (encounter.Status == EncounterResponse.Types.Status.PokemonInventoryFull)
                {
                    if (session.LogicSettings.EvolveAllPokemonAboveIv ||
                        session.LogicSettings.EvolveAllPokemonWithEnoughCandy)
                    {
                        await EvolvePokemonTask.Execute(session, cancellationToken);
                    }

                    if (session.LogicSettings.TransferDuplicatePokemon)
                    {
                        await TransferDuplicatePokemonTask.Execute(session, cancellationToken);
                    }
                    else
                    {
                        session.EventDispatcher.Send(new WarnEvent
                        {
                            Message = session.Translation.GetTranslation(TranslationString.InvFullTransferManually)
                        });
                    }
                }
                else
                {
                    session.EventDispatcher.Send(new WarnEvent
                    {
                        Message =
                            session.Translation.GetTranslation(
                                TranslationString.EncounterProblem, encounter.Status)
                    });
                }

                if (
                    !Equals(catchablePokemon.ElementAtOrDefault(catchablePokemon.Count - 1),
                        pokemon))
                {
                    await Task.Delay(session.LogicSettings.DelayBetweenPokemonCatch, cancellationToken);
                }
            }

            session.EventDispatcher.Send(new SnipeModeEvent { Active = false });
            await Task.Delay(session.LogicSettings.DelayBetweenPlayerActions, cancellationToken);
        }

        public static async Task<bool> CheckPokeballsToSnipe(int minPokeballs, ISession session, CancellationToken cancellationToken)
        {
            var pokeBallsCount = await session.Inventory.GetItemAmountByType(ItemId.ItemPokeBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemGreatBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemUltraBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemMasterBall);

            if (pokeBallsCount < minPokeballs)
            {
                session.EventDispatcher.Send(new SnipeEvent
                {
                    Message =
                        session.Translation.GetTranslation(TranslationString.GotEnoughPokeballsToSnipe, pokeBallsCount,
                            minPokeballs)
                });
                return false;
            }

            return true;
        }
    }
}