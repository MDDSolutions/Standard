using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDDFoundation.Menus;

namespace MDDDataAccess.Menus
{
    /// <summary>Loads menu definitions from MenuSystem using the supplied DBEngine's connection policy.</summary>
    public sealed class DatabaseMenuProvider : IMenuProvider
    {
        private readonly DBEngine engine;
        public DatabaseMenuProvider(DBEngine engine) { this.engine = engine ?? throw new ArgumentNullException(nameof(engine)); }
        public DatabaseMenuProvider(string connectionString) : this(new DBEngine(connectionString, "MenuSystem") { AllowAdHoc = true }) { }

        public async Task<IReadOnlyList<MenuItem>> LoadAsync(string applicationKey, CancellationToken cancellationToken)
        {
            var items = new List<MenuItem>();
            using (var table = await engine.SqlRunQueryWithResultsDataTableAsync(@"SELECT Id, ParentId, Kind, Title, Description, IconKey, Keywords, SortOrder,
TargetKind, TargetTypeName, AssemblyName, ExecutablePath, Arguments, DefaultLaunchMode, AllowNewInstance, ProcedureName
FROM MenuSystem.MenuItem WHERE ApplicationKey = @ApplicationKey AND Retired = 0 ORDER BY SortOrder, Title, Id;",
                false, cancellationToken, -1, "MenuSystem",
                new SqlParameter("@ApplicationKey", SqlDbType.NVarChar, 100) { Value = applicationKey }).ConfigureAwait(false))
            {
                foreach (DataRow row in table.Rows)
                    items.Add(new MenuItem {
                        Id = (int)row[0], ParentId = row.IsNull(1) ? (int?)null : (int)row[1],
                        Kind = (MenuItemKind)(int)row[2], Title = (string)row[3],
                        Description = ReadText(row, 4), IconKey = ReadText(row, 5), Keywords = ReadText(row, 6),
                        SortOrder = (int)row[7], TargetKind = (MenuTargetKind)(int)row[8],
                        TargetTypeName = ReadText(row, 9), AssemblyName = ReadText(row, 10),
                        ExecutablePath = ReadText(row, 11), Arguments = ReadText(row, 12),
                        DefaultLaunchMode = (MenuLaunchMode)(int)row[13], AllowNewInstance = (bool)row[14], ProcedureName = ReadText(row, 15)
                    });
            }
            using (var table = await engine.SqlRunQueryWithResultsDataTableAsync(
                @"SELECT placed.MenuItemId, placed.CategoryId, placed.SortOrder
FROM MenuSystem.MenuItemCategory placed
JOIN MenuSystem.MenuItem item ON item.ApplicationKey = placed.ApplicationKey AND item.Id = placed.MenuItemId
JOIN MenuSystem.MenuItem category ON category.ApplicationKey = placed.ApplicationKey AND category.Id = placed.CategoryId
WHERE placed.ApplicationKey = @ApplicationKey AND item.Retired = 0 AND category.Retired = 0
ORDER BY placed.CategoryId, placed.SortOrder, placed.MenuItemId;",
                false, cancellationToken, -1, "MenuSystem",
                new SqlParameter("@ApplicationKey", SqlDbType.NVarChar, 100) { Value = applicationKey }).ConfigureAwait(false))
            {
                var byId = items.ToDictionary(i => i.Id);
                foreach (DataRow row in table.Rows)
                    if (byId.TryGetValue((int)row[0], out var item))
                        item.Categories.Add(new MenuCategoryRef { CategoryId = (int)row[1], SortOrder = (int)row[2] });
            }
            return MenuDefinition.Validate(items);
        }
        private static string ReadText(DataRow row, int ordinal) => row.IsNull(ordinal) ? null : (string)row[ordinal];
    }
}
