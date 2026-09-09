using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation.Menus
{
    /// <summary>
    /// The write side of a menu definition. Optional: a launcher given no editor shows no editing
    /// affordances, which is how read-only providers (JSON, for instance) stay read-only.
    /// <para>Unlike favourites, everything here is shared - it changes the menu for every user of
    /// the application key.</para>
    /// </summary>
    public interface IMenuEditor
    {
        /// <summary>Inserts when Id is 0, otherwise updates. Returns the item's Id.</summary>
        Task<int> SaveAsync(string applicationKey, MenuItem item, CancellationToken token);

        /// <summary>Retires an item so it no longer appears. Retiring is used rather than deleting
        /// because usage history references menu items; it is reversible. A category with contents
        /// requires <paramref name="reassignMembersTo"/>.</summary>
        Task RetireAsync(string applicationKey, int id, int? reassignMembersTo, CancellationToken token);

        /// <summary>Adds an action to a category, or moves it within one. A null sort order appends.</summary>
        Task SetCategoryAsync(string applicationKey, int menuItemId, int categoryId, int? sortOrder, CancellationToken token);

        /// <summary>Removes one placement. An action's last category cannot be removed.</summary>
        Task RemoveCategoryAsync(string applicationKey, int menuItemId, int categoryId, CancellationToken token);

        /// <summary>Moves a category one place among its siblings; delta is -1 or 1.</summary>
        Task MoveCategoryAsync(string applicationKey, int categoryId, int delta, CancellationToken token);

        /// <summary>Moves an action one place within one of its categories; delta is -1 or 1.</summary>
        Task MoveInCategoryAsync(string applicationKey, int menuItemId, int categoryId, int delta, CancellationToken token);
    }
}
