using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using MDDFoundation.Menus;

namespace MDDDataAccess.Menus
{
    public sealed class DatabaseMenuUserStore : IMenuUserStore
    {
        private readonly DBEngine engine;
        private readonly string identityKey;
        private readonly string displayName;
        private readonly Guid sessionId = Guid.NewGuid();
        private int userId;

        public DatabaseMenuUserStore(DBEngine engine, string identityKey, string displayName)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (string.IsNullOrWhiteSpace(identityKey) || identityKey.Length > 256) throw new ArgumentException("A stable user identity of up to 256 characters is required.", nameof(identityKey));
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256) throw new ArgumentException("A display name of up to 256 characters is required.", nameof(displayName));
            this.identityKey = identityKey; this.displayName = displayName;
        }

        public async Task<MenuUserPreferences> LoadAsync(string applicationKey, CancellationToken token)
        {
            var id = await engine.SqlGetScalarAsync<int>("MenuSystem.User_Open", true, token, -1, "MenuSystem",
                Text("@ApplicationKey", 100, applicationKey), Text("@IdentityKey", 256, identityKey), Text("@DisplayName", 256, displayName)).ConfigureAwait(false);
            var state = new MenuUserPreferences { UserId = id };
            using (var table = await engine.SqlRunQueryWithResultsDataTableAsync("MenuSystem.UserState_Select", true, token, -1, "MenuSystem",
                Number("@UserId", id), Text("@ApplicationKey", 100, applicationKey)).ConfigureAwait(false))
            {
                foreach (DataRow row in table.Rows)
                {
                    int itemId = (int)row["MenuItemId"];
                    if (!row.IsNull("SortOrder")) { state.Favorites.Add(itemId); state.FavoriteOrder[itemId] = (int)row["SortOrder"]; }
                    if (!row.IsNull("LastUsed")) state.LastUsed[itemId] = (DateTime)row["LastUsed"];
                }
            }
            using (var table = await engine.SqlRunQueryWithResultsDataTableAsync("MenuSystem.UserSetting_Select", true, token, -1, "MenuSystem",
                Number("@UserId", id), Text("@ApplicationKey", 100, applicationKey)).ConfigureAwait(false))
            {
                foreach (DataRow row in table.Rows)
                    if (!row.IsNull("Value")) state.Settings[(string)row["Name"]] = (string)row["Value"];
            }
            userId = id;
            return state;
        }
        public Task SetFavouriteAsync(string applicationKey, int menuItemId, bool isFavourite, int? sortOrder, CancellationToken token)
        {
            EnsureLoaded();
            return engine.SqlRunProcedureAsync("MenuSystem.UserFavourites_Set", token, -1, "MenuSystem",
                Number("@UserId", userId), Text("@ApplicationKey", 100, applicationKey), Number("@MenuItemId", menuItemId),
                new SqlParameter("@IsFavourite", SqlDbType.Bit) { Value = isFavourite },
                new SqlParameter("@SortOrder", SqlDbType.Int) { Value = (object)sortOrder ?? DBNull.Value });
        }
        public Task<DateTime> RecordUsageAsync(string applicationKey, int menuItemId, MenuLaunchMode mode, CancellationToken token)
        {
            EnsureLoaded();
            return engine.SqlGetScalarAsync<DateTime>("MenuSystem.UsageLog_Record", true, token, -1, "MenuSystem",
                Number("@UserId", userId), Text("@ApplicationKey", 100, applicationKey), Number("@MenuItemId", menuItemId), Number("@LaunchMode", (int)mode),
                new SqlParameter("@SessionId", SqlDbType.UniqueIdentifier) { Value = sessionId }, Text("@MachineName", 128, Environment.MachineName));
        }
        public Task SetSettingAsync(string applicationKey, string name, string value, CancellationToken token)
        {
            EnsureLoaded();
            return engine.SqlRunProcedureAsync("MenuSystem.UserSetting_Set", token, -1, "MenuSystem",
                Number("@UserId", userId), Text("@ApplicationKey", 100, applicationKey), Text("@Name", 64, name),
                new SqlParameter("@Value", SqlDbType.NVarChar, 400) { Value = (object)value ?? DBNull.Value });
        }
        private void EnsureLoaded() { if (userId <= 0) throw new InvalidOperationException("Load the menu user before changing favourites or recording usage."); }
        private static SqlParameter Text(string name, int size, string value) => new SqlParameter(name, SqlDbType.NVarChar, size) { Value = value };
        private static SqlParameter Number(string name, int value) => new SqlParameter(name, SqlDbType.Int) { Value = value };
    }
}
