using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using MDDFoundation.Menus;

namespace MDDDataAccess.Menus
{
    /// <summary>Writes menu definitions back to MenuSystem through its editing procedures.</summary>
    public sealed class DatabaseMenuEditor : IMenuEditor
    {
        private readonly DBEngine engine;
        public DatabaseMenuEditor(DBEngine engine) { this.engine = engine ?? throw new ArgumentNullException(nameof(engine)); }

        public Task<int> SaveAsync(string applicationKey, MenuItem item, CancellationToken token)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return engine.SqlGetScalarAsync<int>("MenuSystem.MenuItem_Save", true, token, -1, "MenuSystem",
                Key(applicationKey),
                Number("@Id", item.Id > 0 ? item.Id : (int?)null),
                Number("@Kind", (int)item.Kind),
                Text("@Title", 200, item.Title),
                Text("@Description", 1000, item.Description),
                Text("@IconKey", 200, item.IconKey),
                Text("@Keywords", 1000, item.Keywords),
                Number("@SortOrder", item.SortOrder),
                Number("@ParentId", item.ParentId),
                Number("@TargetKind", (int)item.TargetKind),
                Text("@TargetTypeName", 1000, item.TargetTypeName),
                Text("@AssemblyName", 300, item.AssemblyName),
                Text("@ProcedureName", 517, item.ProcedureName),
                Text("@ExecutablePath", 2000, item.ExecutablePath),
                Text("@Arguments", 2000, item.Arguments),
                Number("@DefaultLaunchMode", (int)item.DefaultLaunchMode),
                new SqlParameter("@AllowNewInstance", SqlDbType.Bit) { Value = item.AllowNewInstance });
        }

        public Task RetireAsync(string applicationKey, int id, int? reassignMembersTo, CancellationToken token) =>
            engine.SqlRunProcedureAsync("MenuSystem.MenuItem_Retire", token, -1, "MenuSystem",
                Key(applicationKey), Number("@Id", id), Number("@ReassignMembersTo", reassignMembersTo));

        public Task SetCategoryAsync(string applicationKey, int menuItemId, int categoryId, int? sortOrder, CancellationToken token) =>
            engine.SqlRunProcedureAsync("MenuSystem.MenuItemCategory_Set", token, -1, "MenuSystem",
                Key(applicationKey), Number("@MenuItemId", menuItemId), Number("@CategoryId", categoryId), Number("@SortOrder", sortOrder));

        public Task RemoveCategoryAsync(string applicationKey, int menuItemId, int categoryId, CancellationToken token) =>
            engine.SqlRunProcedureAsync("MenuSystem.MenuItemCategory_Remove", token, -1, "MenuSystem",
                Key(applicationKey), Number("@MenuItemId", menuItemId), Number("@CategoryId", categoryId));

        public Task MoveCategoryAsync(string applicationKey, int categoryId, int delta, CancellationToken token) =>
            engine.SqlRunProcedureAsync("MenuSystem.MenuItem_MoveCategory", token, -1, "MenuSystem",
                Key(applicationKey), Number("@Id", categoryId), Number("@Delta", delta));

        public Task MoveInCategoryAsync(string applicationKey, int menuItemId, int categoryId, int delta, CancellationToken token) =>
            engine.SqlRunProcedureAsync("MenuSystem.MenuItemCategory_Move", token, -1, "MenuSystem",
                Key(applicationKey), Number("@MenuItemId", menuItemId), Number("@CategoryId", categoryId), Number("@Delta", delta));

        private static SqlParameter Key(string applicationKey) => Text("@ApplicationKey", 100, applicationKey);
        private static SqlParameter Text(string name, int size, string value) =>
            new SqlParameter(name, SqlDbType.NVarChar, size) { Value = (object)value ?? DBNull.Value };
        private static SqlParameter Number(string name, int? value) =>
            new SqlParameter(name, SqlDbType.Int) { Value = value.HasValue ? (object)value.Value : DBNull.Value };
    }
}
