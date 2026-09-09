using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation.Menus
{
    /// <summary>A per-user snapshot; persistence belongs to IMenuUserStore.</summary>
    public sealed class MenuUserPreferences
    {
        public int UserId { get; set; }
        public HashSet<int> Favorites { get; } = new HashSet<int>();
        public Dictionary<int, int> FavoriteOrder { get; } = new Dictionary<int, int>();
        public Dictionary<int, DateTime> LastUsed { get; } = new Dictionary<int, DateTime>();
        /// <summary>Small per-user launcher preferences, such as which view opens by default.</summary>
        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public interface IMenuUserStore
    {
        // Registers the current user in simple mode and loads an application-specific snapshot.
        Task<MenuUserPreferences> LoadAsync(string applicationKey, CancellationToken token);
        Task SetFavouriteAsync(string applicationKey, int menuItemId, bool isFavourite, int? sortOrder, CancellationToken token);
        // Records a successful menu launch/activation and returns the database's local timestamp.
        Task<DateTime> RecordUsageAsync(string applicationKey, int menuItemId, MenuLaunchMode mode, CancellationToken token);
        // Stores one preference. A null value clears it.
        Task SetSettingAsync(string applicationKey, string name, string value, CancellationToken token);
    }
}
