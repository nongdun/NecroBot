#region using directives

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Model.Settings;
using PoGo.NecroBot.Logic.PoGoUtils;
using PoGo.NecroBot.Logic.State;
using POGOProtos.Enums;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Pokemon;
using POGOProtos.Networking.Responses;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.Utils;
using System.Data;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class SnipePokemonFromMySqlTask
    {
        public static List<PokemonLocation> LocsVisited = new List<PokemonLocation>();
        private static readonly List<SniperInfo> SnipeLocations = new List<SniperInfo>();
        private static DateTime _lastSnipe = DateTime.MinValue;

        public static async Task<bool> CheckPokeballsToSnipe(int minPokeballs, ISession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Refresh inventory so that the player stats are fresh
            await session.Inventory.RefreshCachedInventory();

            var pokeBallsCount = await session.Inventory.GetItemAmountByType(ItemId.ItemPokeBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemGreatBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemUltraBall);
            pokeBallsCount += await session.Inventory.GetItemAmountByType(ItemId.ItemMasterBall);

            if (pokeBallsCount < minPokeballs)
            {
                session.EventDispatcher.Send(new SnipeEvent
                {
                    Message =
                        session.Translation.GetTranslation(TranslationString.NotEnoughPokeballsToSnipe, pokeBallsCount,
                            minPokeballs)
                });
                return false;
            }

            return true;
        }

        private static bool CheckSnipeConditions(ISession session)
        {
            if (!session.LogicSettings.UseSnipeLimit) return true;

            session.EventDispatcher.Send(new SnipeEvent { Message = session.Translation.GetTranslation(TranslationString.SniperCount, session.Stats.SnipeCount) });
            if (session.Stats.SnipeCount >= session.LogicSettings.SnipeCountLimit)
            {
                if ((DateTime.Now - session.Stats.LastSnipeTime).TotalSeconds > session.LogicSettings.SnipeRestSeconds)
                {
                    session.Stats.SnipeCount = 0;
                }
                else
                {
                    session.EventDispatcher.Send(new SnipeEvent { Message = session.Translation.GetTranslation(TranslationString.SnipeExceeds) });
                    return false;
                }
            }
            return true;
        }

        public static async Task Execute(ISession session, CancellationToken cancellationToken)
        {
            if (_lastSnipe.AddMilliseconds(session.LogicSettings.MinDelayBetweenSnipes) > DateTime.Now)
                return;

            LocsVisited.RemoveAll(q => DateTime.Now > q.TimeStampAdded.AddMinutes(15));
            SnipeLocations.RemoveAll(x => DateTime.Now > x.TimeStampAdded.AddMinutes(15));

            if (await CheckPokeballsToSnipe(session.LogicSettings.MinPokeballsToSnipe, session, cancellationToken))
            {
                if (session.LogicSettings.PokemonToSnipe != null)
                {
                    List<PokemonId> pokemonIds = new List<PokemonId>();
                    if (session.LogicSettings.SnipePokemonNotInPokedex)
                    {
                        var PokeDex = await session.Inventory.GetPokeDexItems();
                        var pokemonOnlyList = session.LogicSettings.PokemonToSnipe.Pokemon;
                        var capturedPokemon = PokeDex.Where(i => i.InventoryItemData.PokedexEntry.TimesCaptured >= 1).Select(i => i.InventoryItemData.PokedexEntry.PokemonId);
                        var pokemonToCapture = Enum.GetValues(typeof(PokemonId)).Cast<PokemonId>().Except(capturedPokemon);
                        pokemonIds = pokemonOnlyList.Union(pokemonToCapture).ToList();
                    }
                    else
                    {
                        pokemonIds = session.LogicSettings.PokemonToSnipe.Pokemon;
                    }

                    if (session.LogicSettings.GetSniperInfoFromMysql)
                    {
                        var _locationsToSnipe = GetSniperInfoFrom_Mysql(session, pokemonIds);
                        if (_locationsToSnipe != null && _locationsToSnipe.Any())
                        {
                            foreach (var location in _locationsToSnipe)
                            {
                                if (!LocsVisited.Contains(new PokemonLocation(location.Latitude, location.Longitude)))
                                {
                                    session.EventDispatcher.Send(new SnipeScanEvent
                                    {
                                        Bounds = new Location(location.Latitude, location.Longitude),
                                        PokemonId = location.Id,
                                        Source = "MySql",
                                        Iv = location.IV
                                    });

                                    if (!await CheckPokeballsToSnipe(session.LogicSettings.MinPokeballsWhileSnipe + 1, session, cancellationToken))
                                        return;

                                    if (!CheckSnipeConditions(session) && location.IV < 98) return;
                                    await Snipe(session, pokemonIds, location.Latitude, location.Longitude, cancellationToken);
                                }
                            }
                        }
                    }
                }
            }
        }

        public static async Task Snipe(ISession session, IEnumerable<PokemonId> pokemonIds, double latitude,
            double longitude, CancellationToken cancellationToken)
        {
            if (LocsVisited.Contains(new PokemonLocation(latitude, longitude)))
                return;

            var CurrentLatitude = session.Client.CurrentLatitude;
            var CurrentLongitude = session.Client.CurrentLongitude;
            var catchedPokemon = false;

            session.EventDispatcher.Send(new SnipeModeEvent { Active = true });

            List<MapPokemon> catchablePokemon = new List<MapPokemon>();
            
            for(int i=0;i<5;i++)
            { 
                try
                {
                    await session.Client.Player.UpdatePlayerLocation(latitude, longitude, session.Client.CurrentAltitude);

                session.EventDispatcher.Send(new UpdatePositionEvent
                {
                    Longitude = longitude,
                    Latitude = latitude
                });

                    var mapObjects = session.Client.Map.GetMapObjects().Result;
                    catchablePokemon =
                        mapObjects.Item1.MapCells.SelectMany(q => q.CatchablePokemons)
                            .Where(q => pokemonIds.Contains(q.PokemonId))
                            .OrderByDescending(pokemon => PokemonInfo.CalculateMaxCpMultiplier(pokemon.PokemonId))
                            .ToList();
                }
                finally
                {
                    await session.Client.Player.UpdatePlayerLocation(CurrentLatitude, CurrentLongitude, session.Client.CurrentAltitude);
                }

                if(catchablePokemon.Count > 0)
                {
                    Logger.Write($"Retry times: {i} at [{latitude.ToString("0.000000")},{longitude.ToString("0.000000")}]", LogLevel.Info);
                    break;
                }

                await Task.Delay(200, cancellationToken);
            }

            if (catchablePokemon.Count == 0)
            {
                // Pokemon not found but we still add to the locations visited, so we don't keep sniping
                // locations with no pokemon.
                if (!LocsVisited.Contains(new PokemonLocation(latitude, longitude)))
                    LocsVisited.Add(new PokemonLocation(latitude, longitude));
            }

            foreach (var pokemon in catchablePokemon)
            {
                EncounterResponse encounter;
                try
                {
                    await session.Client.Player.UpdatePlayerLocation(latitude, longitude, session.Client.CurrentAltitude);

                    encounter = session.Client.Encounter.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnPointId).Result;
                }
                finally
                {
                    await session.Client.Player.UpdatePlayerLocation(CurrentLatitude, CurrentLongitude, session.Client.CurrentAltitude);
                }

                if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                {

                    if (!LocsVisited.Contains(new PokemonLocation(latitude, longitude)))
                        LocsVisited.Add(new PokemonLocation(latitude, longitude));
                    //Also add exact pokemon location to LocsVisited, some times the server one differ a little.
                    if (!LocsVisited.Contains(new PokemonLocation(pokemon.Latitude, pokemon.Longitude)))
                        LocsVisited.Add(new PokemonLocation(pokemon.Latitude, pokemon.Longitude));

                    session.EventDispatcher.Send(new UpdatePositionEvent
                    {
                        Latitude = CurrentLatitude,
                        Longitude = CurrentLongitude
                    });

                    await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon);
                    catchedPokemon = true;
                }
                else if (encounter.Status == EncounterResponse.Types.Status.PokemonInventoryFull)
                {
                    if (session.LogicSettings.EvolveAllPokemonAboveIv ||
                        session.LogicSettings.EvolveAllPokemonWithEnoughCandy ||
                        session.LogicSettings.UseLuckyEggsWhileEvolving ||
                        session.LogicSettings.KeepPokemonsThatCanEvolve)
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

                if (!Equals(catchablePokemon.ElementAtOrDefault(catchablePokemon.Count - 1), pokemon))
                    await Task.Delay(session.LogicSettings.DelayBetweenPokemonCatch, cancellationToken);
            }

            //if (!catchedPokemon)
            //{
            //    session.EventDispatcher.Send(new SnipeEvent
            //    {
            //        Message = session.Translation.GetTranslation(TranslationString.NoPokemonToSnipe)
            //    });
            //}

            _lastSnipe = DateTime.Now;

            if (catchedPokemon)
            {
                session.Stats.LastSnipeTime = _lastSnipe;
	            await Task.Delay(session.LogicSettings.DelayBetweenPlayerActions, cancellationToken);
            }
            session.EventDispatcher.Send(new SnipeModeEvent { Active = false });

        }

        private static List<SniperInfo> GetSniperInfoFrom_Mysql(ISession session, List<PokemonId> pokemonIds)
        {
            string sql = "SELECT * FROM active_pokemons ORDER BY iv DESC";

            //Logger.Write(sql, LogLevel.Info);
            DataTable pokemonList = new DataTable();
            try
            {
                pokemonList = MySqlHelper.ExecuteDataTable(sql);
            }
            catch (MySql.Data.MySqlClient.MySqlException e)
            {
                Logger.Write($"mysql read error {e.ToString()}", LogLevel.Warning);
            }

            if(pokemonList.Rows.Count > 0)
            {
                var mysqlLocationsToSnipe = new List<SniperInfo>();
                foreach (DataRow dr in pokemonList.Rows)
                {
                    try
                    { 
                        SniperInfo pokemon = new SniperInfo();
                        pokemon.Id = (PokemonId)dr["PokemonId"];
                        pokemon.Latitude = Convert.ToDouble(dr["Latitude"], CultureInfo.InvariantCulture);
                        pokemon.Longitude = Convert.ToDouble(dr["Longitude"], CultureInfo.InvariantCulture);
                        pokemon.EncounterId = Convert.ToUInt64(dr["EncounterId"]);
                        pokemon.SpawnPointId = dr["SpawnPointId"].ToString();
                        pokemon.IV = (double)dr["iv"];
                        //pokemon.Move1 = (PokemonMove)dr["Move1"];
                        //pokemon.Move2 = (PokemonMove)dr["Move2"];
                        pokemon.ExpirationTimestamp = (DateTime)dr["ExpirationTimestamp"];
                        mysqlLocationsToSnipe.Add(pokemon);
                    }
                    catch (Exception e)
                    {
                        Logger.Write($"mysql sniper info convert error:{e.ToString()}", LogLevel.Warning);
                    }
                }

                var locationsToSnipe = new List<SniperInfo>();
                var status = "";
                foreach (var q in mysqlLocationsToSnipe)
                {
                    if (q.IV > 98) { locationsToSnipe.Add(q);  continue; }
                    if (q.IV< session.LogicSettings.MinIvPercentageToCatch) { continue; }

                    if (!session.LogicSettings.UseTransferIvForSnipe || (q.IV == 0 && !session.LogicSettings.SnipeIgnoreUnknownIv) || (q.IV >= session.Inventory.GetPokemonTransferFilter(q.Id).KeepMinIvPercentage))
                    {
                        if (!LocsVisited.Contains(new PokemonLocation(q.Latitude, q.Longitude)))
                        {
                            if (q.ExpirationTimestamp != default(DateTime) && q.ExpirationTimestamp > new DateTime(2016) && q.ExpirationTimestamp > DateTime.Now.AddSeconds(20))
                            {
                                if (q.Id == PokemonId.Missingno || pokemonIds.Contains(q.Id))
                                {
                                    locationsToSnipe.Add(q);
                                    status = "Snipe! Let's Go!";
                                    var message = "MySql: Found a " + q.Id + " in " + q.Latitude.ToString("0.0000") + "," + q.Longitude.ToString("0.0000") + " Time Remain:" +
                                        (q.ExpirationTimestamp - DateTime.Now).TotalSeconds.ToString("0") + "s " +
                                        " Status: " + status;

                                    Logger.Write(message, LogLevel.Info);
                                    session.EventDispatcher.Send(new SnipeEvent { Message = message });
                                }
                                else
                                {
                                    status = "Not User Selected Pokemon";
                                }
                            }
                            else
                            {
                                status = "Expired";
                            }
                        }
                        else
                        {
                            status = "Already Visited";
                        }
                    }
                    else
                    {
                        status = "IV too low or user choosed ignore unknown IV pokemon";
                    }
                }
                return locationsToSnipe.OrderBy(q => q.ExpirationTimestamp).ToList();
            }
            else
                return null;
        }

    }
}
