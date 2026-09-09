using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation.Menus
{
    public enum MenuItemKind { Category, Action }
    public enum MenuLaunchMode { ActivateExistingOrCreate, CreateNew }
    public enum MenuTargetKind { Control, Process, StoredProcedure }

    /// <summary>Places an action in one category, with the order it takes within that category.</summary>
    public sealed class MenuCategoryRef
    {
        public int CategoryId { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class MenuItem
    {
        public int Id { get; set; }
        // Categories only: the parent category. Actions use Categories instead and leave this null.
        public int? ParentId { get; set; }
        // Actions only: every category the action appears in. An action belongs to at least one.
        public List<MenuCategoryRef> Categories { get; set; } = new List<MenuCategoryRef>();
        public MenuItemKind Kind { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }
        public string Keywords { get; set; }
        public int SortOrder { get; set; }
        public MenuTargetKind TargetKind { get; set; }
        // Assembly-qualified names are supported; AssemblyName is optional for unloaded libraries.
        public string TargetTypeName { get; set; }
        public string AssemblyName { get; set; }
        public string ProcedureName { get; set; }
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }
        public MenuLaunchMode DefaultLaunchMode { get; set; }
        public bool AllowNewInstance { get; set; }
    }

    public interface IMenuProvider
    {
        Task<IReadOnlyList<MenuItem>> LoadAsync(string applicationKey, CancellationToken cancellationToken);
    }

    public interface IMenuTargetIdentity
    {
        string MenuTargetKey { get; }
    }

    public interface IMenuDispatcher
    {
        // Called on the launcher's UI thread. Throws on failure; history is recorded only on success.
        void Launch(MenuItem item, MenuLaunchMode mode);
    }

    public static class MenuDefinition
    {
        public static IReadOnlyList<MenuItem> Validate(IEnumerable<MenuItem> source)
        {
            var items = source?.ToList() ?? throw new ArgumentNullException(nameof(source));
            var byId = new Dictionary<int, MenuItem>();
            foreach (var item in items)
            {
                if (item == null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Title))
                    throw new InvalidDataException("Menu items require a positive ID and a title.");
                if (byId.ContainsKey(item.Id)) throw new InvalidDataException($"Duplicate menu ID {item.Id}.");
                byId.Add(item.Id, item);
                if (!Enum.IsDefined(typeof(MenuItemKind), item.Kind) ||
                    !Enum.IsDefined(typeof(MenuLaunchMode), item.DefaultLaunchMode) ||
                    !Enum.IsDefined(typeof(MenuTargetKind), item.TargetKind))
                    throw new InvalidDataException($"Invalid menu enum for {item.Title}.");
                if (item.Kind == MenuItemKind.Category)
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetTypeName) || !string.IsNullOrWhiteSpace(item.ExecutablePath) || !string.IsNullOrWhiteSpace(item.ProcedureName))
                        throw new InvalidDataException($"Category {item.Title} cannot have a launch target.");
                    if (item.Categories != null && item.Categories.Count > 0)
                        throw new InvalidDataException($"Category {item.Title} nests through ParentId, not Categories.");
                }
                else
                {
                    if (item.TargetKind == MenuTargetKind.StoredProcedure ? string.IsNullOrWhiteSpace(item.ProcedureName) : item.TargetKind == MenuTargetKind.Control ? string.IsNullOrWhiteSpace(item.TargetTypeName) : string.IsNullOrWhiteSpace(item.ExecutablePath))
                        throw new InvalidDataException($"Missing launch target for {item.Title}.");
                    if (item.ParentId.HasValue)
                        throw new InvalidDataException($"Action {item.Title} is placed through Categories, not ParentId.");
                    if (item.Categories == null || item.Categories.Count == 0)
                        throw new InvalidDataException($"Action {item.Title} must belong to at least one category.");
                }
            }
            foreach (var item in items)
            {
                var seen = new HashSet<int> { item.Id };
                var current = item;
                while (current.ParentId.HasValue)
                {
                    if (!byId.TryGetValue(current.ParentId.Value, out current) || current.Kind != MenuItemKind.Category)
                        throw new InvalidDataException($"Invalid parent for {item.Title}.");
                    if (!seen.Add(current.Id)) throw new InvalidDataException($"Category cycle for {item.Title}.");
                }
            }
            foreach (var item in items.Where(i => i.Kind == MenuItemKind.Action))
            {
                var placed = new HashSet<int>();
                foreach (var reference in item.Categories)
                {
                    if (!byId.TryGetValue(reference.CategoryId, out var category) || category.Kind != MenuItemKind.Category)
                        throw new InvalidDataException($"Unknown category {reference.CategoryId} for {item.Title}.");
                    if (!placed.Add(reference.CategoryId))
                        throw new InvalidDataException($"{item.Title} is listed twice in {category.Title}.");
                }
            }
            foreach (var item in items.Where(i => i.Kind == MenuItemKind.Action && i.TargetKind == MenuTargetKind.Process))
                if (item.DefaultLaunchMode != MenuLaunchMode.CreateNew)
                    throw new InvalidDataException($"External application {item.Title} must use CreateNew.");
            return items.AsReadOnly();
        }
    }

    public sealed class JsonMenuProvider : IMenuProvider
    {
        private readonly string path;
        public JsonMenuProvider(string path) { this.path = path ?? throw new ArgumentNullException(nameof(path)); }
        public async Task<IReadOnlyList<MenuItem>> LoadAsync(string applicationKey, CancellationToken cancellationToken)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            using (var stream = File.OpenRead(path))
            {
                var definitions = await JsonSerializer.DeserializeAsync<Dictionary<string, List<MenuItem>>>(stream, options, cancellationToken).ConfigureAwait(false);
                if (definitions == null || !definitions.TryGetValue(applicationKey, out var items))
                    throw new InvalidDataException($"No menu defined for '{applicationKey}'.");
                return MenuDefinition.Validate(items);
            }
        }
    }
}
