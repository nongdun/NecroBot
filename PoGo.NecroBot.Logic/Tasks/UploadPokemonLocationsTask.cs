using PoGo.NecroBot.Logic.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Utils;
using PoGo.NecroBot.Logic.Logging;

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class UploadPokemonLocationsTask
    {
        public static async Task Execute(ISession session, SniperInfo pokemonInfo, CancellationToken cancellationToken)
        {
            string sql = "INSERT INTO pokemon_list (EncounterId, Latitude, Longitude, PokemonId, SpawnPointId, IV, ExpirationTimestamp, Verified) VALUES (" +
                         pokemonInfo.EncounterId.ToString() + ", " +
                         pokemonInfo.Latitude.ToString("0.000000") + ", " +
                         pokemonInfo.Longitude.ToString("0.000000") + ", " +
                         ((int)pokemonInfo.Id).ToString() + ", \"" +
                         pokemonInfo.SpawnPointId.ToString() + "\", " +
                         pokemonInfo.IV.ToString("0.00") + ", \"" +
                         pokemonInfo.ExpirationTimestamp.ToString("yyyy-MM-dd HH:mm:ss") + "\", 1)";

            //Logger.Write(sql, LogLevel.Info);
            int result = 0;
            try
            {
                 result = MySqlHelper.ExecuteNonQuery(sql);
            }
            catch (MySql.Data.MySqlClient.MySqlException e)
            {
                Logger.Write($"mysql execute error {e.ToString()}", LogLevel.Warning);
            }

            //Logger.Write($"mysql execute result {result.ToString()}", LogLevel.Info);
        }
    }
}
