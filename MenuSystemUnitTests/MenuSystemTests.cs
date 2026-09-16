using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MDDFoundation.Menus;
using MDDWinForms;
using MDDWinForms.Menus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MenuItem = MDDFoundation.Menus.MenuItem;

namespace MenuSystemUnitTests
{
    /// <summary>
    /// Covers the menu system without a database or a running application: definition validation,
    /// the WinForms dispatcher, and the launcher driven through in-memory store and editor fakes.
    /// </summary>
    [TestClass]
    public class MenuSystemTests
    {
        // Categories 1 and 2; actions 10..12, with 10 in both categories.
        private static List<MenuItem> Definition()
        {
            var searching = new MenuItem { Id = 1, Kind = MenuItemKind.Category, Title = "Searching", SortOrder = 1 };
            var performers = new MenuItem { Id = 2, Kind = MenuItemKind.Category, Title = "Performers", SortOrder = 2 };
            var performerSearch = Action(10, "Performer Search");
            performerSearch.Categories.Add(new MenuCategoryRef { CategoryId = 1, SortOrder = 2 });
            performerSearch.Categories.Add(new MenuCategoryRef { CategoryId = 2, SortOrder = 1 });
            var sceneSearch = Action(11, "Scene Search");
            sceneSearch.Categories.Add(new MenuCategoryRef { CategoryId = 1, SortOrder = 1 });
            var performerDetails = Action(12, "Performer Details");
            performerDetails.Categories.Add(new MenuCategoryRef { CategoryId = 2, SortOrder = 2 });
            return new List<MenuItem> { searching, performers, performerSearch, sceneSearch, performerDetails };
        }
        private static MenuItem Action(int id, string title) => new MenuItem {
            Id = id, Kind = MenuItemKind.Action, Title = title, AllowNewInstance = true,
            TargetTypeName = typeof(TestForm).AssemblyQualifiedName
        };

        // WinForms needs a single-threaded apartment; the test host does not guarantee one.
        private static void OnStaThread(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // Bounded: a UI test that stops pumping must fail, never hang the whole run.
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                thread.Abort();
                Assert.Fail("the test did not finish within 30 seconds");
            }
            if (failure != null) throw new AssertFailedException(failure.Message, failure);
        }
        private static T Field<T>(MenuLauncherForm form, string name) =>
            (T)typeof(MenuLauncherForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        private static void Invoke(MenuLauncherForm form, string name, params object[] args) =>
            ((Task)typeof(MenuLauncherForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, args)).GetAwaiter().GetResult();
        private static TreeNode Node(TreeView tree, object tag) =>
            Flatten(tree.Nodes).First(n => Equals(n.Tag, tag));
        private static IEnumerable<TreeNode> Flatten(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (var deeper in Flatten(node.Nodes)) yield return deeper;
            }
        }
        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var deeper in Descendants(child)) yield return deeper;
            }
        }

        #region Definition validation

        [TestMethod]
        public void ValidateAcceptsCategoriesAndActions() =>
            Assert.AreEqual(5, MenuDefinition.Validate(Definition()).Count);

        [TestMethod]
        public void ValidateRejectsCategoryCycle()
        {
            var items = Definition();
            items[0].ParentId = items[0].Id;
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void ValidateRejectsUnknownCategory()
        {
            var items = Definition();
            items[2].Categories[0].CategoryId = 99;
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void ValidateRejectsActionWithParentId()
        {
            var items = Definition();
            items[2].ParentId = 1;
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void ValidateRejectsActionInNoCategory()
        {
            var items = Definition();
            items[3].Categories.Clear();
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void ValidateRejectsDuplicatePlacement()
        {
            var items = Definition();
            items[3].Categories.Add(new MenuCategoryRef { CategoryId = 1, SortOrder = 5 });
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void ValidateRequiresExternalProgramsToOpenANewWindow()
        {
            var items = Definition();
            items[3].TargetKind = MenuTargetKind.Process;
            items[3].TargetTypeName = null;
            items[3].ExecutablePath = @"C:\Windows\notepad.exe";
            items[3].DefaultLaunchMode = MenuLaunchMode.ActivateExistingOrCreate;
            Assert.ThrowsException<InvalidDataException>(() => MenuDefinition.Validate(items));
        }

        [TestMethod]
        public void JsonProviderLoadsCategoriesAndPlacements()
        {
            var path = Path.Combine(Path.GetTempPath(), "MenuTests-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, @"{ ""App"": [
                { ""Id"": 1, ""Kind"": ""Category"", ""Title"": ""Only"" },
                { ""Id"": 2, ""Kind"": ""Action"", ""Title"": ""Thing"", ""TargetTypeName"": ""System.Windows.Forms.Form"",
                  ""Categories"": [ { ""CategoryId"": 1, ""SortOrder"": 3 } ] } ] }");
            try
            {
                var loaded = new JsonMenuProvider(path).LoadAsync("App", CancellationToken.None).GetAwaiter().GetResult();
                var action = loaded.Single(i => i.Kind == MenuItemKind.Action);
                Assert.AreEqual(1, action.Categories.Single().CategoryId);
                Assert.AreEqual(3, action.Categories.Single().SortOrder);
                Assert.IsNull(action.ParentId);
            }
            finally { File.Delete(path); }
        }

        #endregion

        #region Dispatcher

        [TestMethod]
        public void DispatcherReusesAWindowAndAppliesTheQualifierOnce() => OnStaThread(() =>
        {
            MDDForms.InstanceQualifier = "Q";
            var dispatcher = new WinFormsMenuDispatcher();
            var item = Action(10, "Test");
            try
            {
                dispatcher.Launch(item, MenuLaunchMode.ActivateExistingOrCreate);
                dispatcher.Launch(item, MenuLaunchMode.ActivateExistingOrCreate);
                Assert.AreEqual(1, Application.OpenForms.Count);
                Assert.AreEqual("Q Test", Application.OpenForms[0].Text);
                dispatcher.Launch(item, MenuLaunchMode.CreateNew);
                Assert.AreEqual(2, Application.OpenForms.Count);
            }
            finally { CloseAll(); MDDForms.InstanceQualifier = ""; }
        });

        [TestMethod]
        public void DispatcherWrapsAControlAndCanSkipTheQualifier() => OnStaThread(() =>
        {
            MDDForms.InstanceQualifier = "Q";
            var item = Action(10, "Test");
            item.TargetTypeName = typeof(TestControl).AssemblyQualifiedName;
            try
            {
                var dispatcher = new WinFormsMenuDispatcher();
                dispatcher.Launch(item, MenuLaunchMode.ActivateExistingOrCreate);
                dispatcher.Launch(item, MenuLaunchMode.ActivateExistingOrCreate);
                Assert.AreEqual(1, Application.OpenForms.Count);
                Assert.IsInstanceOfType(Application.OpenForms[0], typeof(ControlForm));
                Assert.AreEqual("Q Test", Application.OpenForms[0].Text);
                CloseAll();
                new WinFormsMenuDispatcher { UseInstanceQualifier = false }.Launch(item, MenuLaunchMode.CreateNew);
                Assert.AreEqual("Test", Application.OpenForms[0].Text);
            }
            finally { CloseAll(); MDDForms.InstanceQualifier = ""; }
        });

        private static void CloseAll()
        {
            foreach (Form open in Application.OpenForms.Cast<Form>().ToArray()) open.Dispose();
        }

        #endregion

        #region Launcher

        private static void WithLauncher(FakeStore store, Action<MenuLauncherForm, TreeView, ListView> body,
            IReadOnlyList<MenuItem> definition = null) => OnStaThread(() =>
        {
            var items = definition ?? Definition();
            using (var form = new MenuLauncherForm(new StaticProvider(items), new WinFormsMenuDispatcher(), "App", store))
            {
                form.Show();
                Application.DoEvents();
                try { body(form, Field<TreeView>(form, "categories"), Field<ListView>(form, "results")); }
                finally { form.Close(); CloseAll(); }
            }
        });

        [TestMethod]
        public void EveryItemViewIsLastAndListsEverything() => WithLauncher(new FakeStore(), (form, tree, list) =>
        {
            Assert.AreEqual("(All Items)", tree.Nodes[tree.Nodes.Count - 1].Text);
            tree.SelectedNode = Node(tree, "all");
            Assert.AreEqual(3, list.Items.Count);
        });

        [TestMethod]
        public void WithoutFavouritesOrHistoryItOpensOnTheFirstCategory() =>
            WithLauncher(new FakeStore(), (form, tree, list) => Assert.AreEqual(1, tree.SelectedNode.Tag));

        [TestMethod]
        public void FavouritesOpenFirstWhenThereAreSome()
        {
            var store = new FakeStore();
            store.State.Favorites.Add(11);
            store.State.FavoriteOrder[11] = 1;
            WithLauncher(store, (form, tree, list) => Assert.AreEqual("favorites", tree.SelectedNode.Tag));
        }

        [TestMethod]
        public void AStoredDefaultViewWinsOverFavourites()
        {
            var store = new FakeStore();
            store.State.Favorites.Add(11);
            store.State.Settings["DefaultView"] = "category:2";
            WithLauncher(store, (form, tree, list) => Assert.AreEqual(2, tree.SelectedNode.Tag));
        }

        [TestMethod]
        public void ChoosingADefaultViewStoresItForTheUser()
        {
            var store = new FakeStore();
            WithLauncher(store, (form, tree, list) =>
            {
                tree.SelectedNode = Node(tree, 2);
                Invoke(form, "SetDefaultViewAsync");
                CollectionAssert.Contains(store.Calls, "setting:DefaultView=category:2");
            });
        }

        [TestMethod]
        public void AnItemInTwoCategoriesAppearsInBothButIsListedOnceElsewhere() =>
            WithLauncher(new FakeStore(), (form, tree, list) =>
            {
                tree.SelectedNode = Node(tree, 1);
                Assert.IsTrue(list.Items.Cast<ListViewItem>().Any(r => ((MenuItem)r.Tag).Id == 10));
                tree.SelectedNode = Node(tree, 2);
                Assert.IsTrue(list.Items.Cast<ListViewItem>().Any(r => ((MenuItem)r.Tag).Id == 10));
                tree.SelectedNode = Node(tree, "all");
                Assert.AreEqual(1, list.Items.Cast<ListViewItem>().Count(r => ((MenuItem)r.Tag).Id == 10));
            });

        [TestMethod]
        public void EachCategoryHasItsOwnOrderAndItsOwnPathInTheColumn() =>
            WithLauncher(new FakeStore(), (form, tree, list) =>
            {
                tree.SelectedNode = Node(tree, 1);
                Assert.AreEqual(11, ((MenuItem)list.Items[0].Tag).Id, "Scene Search sorts first in Searching");
                Assert.AreEqual("Searching", list.Items[1].SubItems[1].Text);
                tree.SelectedNode = Node(tree, 2);
                Assert.AreEqual(10, ((MenuItem)list.Items[0].Tag).Id, "Performer Search sorts first in Performers");
                Assert.AreEqual("Performers", list.Items[0].SubItems[1].Text);
                tree.SelectedNode = Node(tree, "all");
                Assert.AreEqual("Performers; Searching",
                    list.Items.Cast<ListViewItem>().First(r => ((MenuItem)r.Tag).Id == 10).SubItems[1].Text);
            });

        [TestMethod]
        public void SearchMatchesAnyOfAnItemsCategories() =>
            WithLauncher(new FakeStore(), (form, tree, list) =>
            {
                Field<TextBox>(form, "search").Text = "Performers";
                Assert.IsTrue(list.Items.Cast<ListViewItem>().Any(r => ((MenuItem)r.Tag).Id == 10));
            });

        [TestMethod]
        public void TogglingAFavouriteWritesThroughTheStoreAndReloads()
        {
            var store = new FakeStore();
            WithLauncher(store, (form, tree, list) =>
            {
                tree.SelectedNode = Node(tree, 1);
                list.Items[0].Selected = true;
                var id = ((MenuItem)list.Items[0].Tag).Id;
                typeof(MenuLauncherForm).GetMethod("ToggleFavorite", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);
                Application.DoEvents();
                CollectionAssert.Contains(store.Calls, id + ":True");
                Assert.IsTrue(store.State.Favorites.Contains(id));
            });
        }

        [TestMethod]
        public void ReorderingIsInertOutsideACategory()
        {
            var store = new FakeStore();
            var editor = new FakeEditor();
            WithLauncher(store, (form, tree, list) =>
            {
                form.Editor = editor;
                tree.SelectedNode = Node(tree, "all");
                list.Items[0].Selected = true;
                Invoke(form, "MoveItemAsync", -1);
                Assert.AreEqual(0, editor.Calls.Count);
            });
        }

        [TestMethod]
        public void ReorderingNamesTheCategoryBeingBrowsedAndKeepsTheSelection()
        {
            var store = new FakeStore();
            var editor = new FakeEditor();
            WithLauncher(store, (form, tree, list) =>
            {
                form.Editor = editor;
                tree.SelectedNode = Node(tree, 2);
                list.Items[1].Selected = true;
                var moving = ((MenuItem)list.Items[1].Tag).Id;
                Invoke(form, "MoveItemAsync", -1);
                CollectionAssert.Contains(editor.Calls, "moveitem:" + moving + ":2:-1");
                Assert.AreEqual(moving, ((MenuItem)list.SelectedItems[0].Tag).Id, "selection survives the reload");
                Assert.AreEqual(2, tree.SelectedNode.Tag, "the tree keeps its selection too");
            });
        }

        [TestMethod]
        public void ReorderingACategoryNamesTheSelectedCategory()
        {
            var editor = new FakeEditor();
            WithLauncher(new FakeStore(), (form, tree, list) =>
            {
                form.Editor = editor;
                tree.SelectedNode = Node(tree, 2);
                Invoke(form, "MoveCategoryAsync", 1);
                CollectionAssert.Contains(editor.Calls, "movecat:2:1");
            });
        }

        [TestMethod]
        public void ThereIsNoAddButtonWithoutAnEditor() =>
            WithLauncher(new FakeStore(), (form, tree, list) =>
            {
                var add = Descendants(form).OfType<Button>().FirstOrDefault(b => b.Text == "Add...");
                Assert.IsNotNull(add);
                Assert.IsFalse(add.Visible);
            });

        #endregion

        #region Edit dialog

        // It once shipped collapsed to a sliver: a Form that auto-sizes around docked children has
        // no width to hand them. Asserting on the size catches that; compiling never will.
        [TestMethod]
        public void EditDialogOpensUsableAndResizable() => OnStaThread(() =>
        {
            var items = Definition();
            using (var dialog = new MenuItemEditDialog(items.Single(i => i.Id == 10), items))
            {
                dialog.Show();
                Application.DoEvents();
                Assert.IsTrue(dialog.ClientSize.Width >= 560 && dialog.ClientSize.Height >= 400, "opens at a usable size");
                Assert.AreEqual(FormBorderStyle.Sizable, dialog.FormBorderStyle);
                var boxes = Descendants(dialog).OfType<TextBox>().Where(t => t.Visible).ToArray();
                Assert.IsTrue(boxes.Length >= 5 && boxes.All(t => t.Width > 200), "fields are not collapsed");
                var checklist = Descendants(dialog).OfType<CheckedListBox>().Single();
                Assert.IsTrue(checklist.Height > 100 && checklist.Width > 200, "category list is usable");
                dialog.Close();
            }
        });

        [TestMethod]
        public void EditDialogShowsOnlyTheFieldsTheTargetKindAllows() => OnStaThread(() =>
        {
            var items = Definition();
            using (var dialog = new MenuItemEditDialog(items.Single(i => i.Id == 10), items))
            {
                dialog.Show();
                Application.DoEvents();
                Func<string, Label> caption = text => Descendants(dialog).OfType<Label>().First(l => l.Text == text);
                Assert.IsTrue(caption("Type name").Visible);
                Assert.IsFalse(caption("Program").Visible);
                var kind = Descendants(dialog).OfType<ComboBox>().First();
                kind.SelectedIndex = (int)MenuTargetKind.Process;
                Application.DoEvents();
                Assert.IsFalse(caption("Type name").Visible);
                Assert.IsTrue(caption("Program").Visible);
                var launchMode = Descendants(dialog).OfType<ComboBox>().ElementAt(1);
                Assert.IsFalse(launchMode.Enabled, "an external program cannot reuse a window");
                Assert.AreEqual((int)MenuLaunchMode.CreateNew, launchMode.SelectedIndex);
                dialog.Close();
            }
        });

        // A new item has no id until the editor saves it, but MenuDefinition.Validate demands a
        // positive one - which made every "New menu item..." refuse to commit.
        [TestMethod]
        public void ANewItemCommitsEvenThoughItHasNoIdYet() => OnStaThread(() =>
        {
            var items = Definition();
            var fresh = new MenuItem { Kind = MenuItemKind.Action, Title = "" };
            fresh.Categories.Add(new MenuCategoryRef { CategoryId = 1 });
            using (var dialog = new MenuItemEditDialog(fresh, items))
            {
                dialog.Show();
                Application.DoEvents();
                DialogField(dialog, "title").Text = "Screen Pixel Probe";
                DialogField(dialog, "targetTypeName").Text = "MDDWinForms.ctlScreenPixelProbe";
                DialogField(dialog, "assemblyName").Text = "MDDWinForms";

                var committed = (bool)typeof(MenuItemEditDialog)
                    .GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, null);

                Assert.IsTrue(committed, DialogLabel(dialog));
                Assert.AreEqual(0, dialog.Result.Id, "it stays unsaved; the editor assigns the id");
                Assert.AreEqual("Screen Pixel Probe", dialog.Result.Title);
                Assert.AreEqual(1, dialog.Result.Categories.Single().CategoryId);
                dialog.Close();
            }
        });

        [TestMethod]
        public void ANewItemStillNeedsATitle() => OnStaThread(() =>
        {
            var items = Definition();
            var fresh = new MenuItem { Kind = MenuItemKind.Action, Title = "" };
            fresh.Categories.Add(new MenuCategoryRef { CategoryId = 1 });
            using (var dialog = new MenuItemEditDialog(fresh, items))
            {
                dialog.Show();
                Application.DoEvents();
                DialogField(dialog, "targetTypeName").Text = "MDDWinForms.ctlScreenPixelProbe";
                var committed = (bool)typeof(MenuItemEditDialog)
                    .GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, null);
                Assert.IsFalse(committed, "a blank title is still rejected");
                dialog.Close();
            }
        });

        private static TextBox DialogField(MenuItemEditDialog dialog, string name) =>
            (TextBox)typeof(MenuItemEditDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(dialog);
        private static string DialogLabel(MenuItemEditDialog dialog) =>
            Descendants(dialog).OfType<Label>().Where(l => l.ForeColor == Color.Firebrick)
                .Select(l => l.Text).FirstOrDefault() ?? "no reason reported";

        #endregion

        #region Fakes

        private sealed class StaticProvider : IMenuProvider
        {
            private readonly IReadOnlyList<MenuItem> items;
            public StaticProvider(IReadOnlyList<MenuItem> items) { this.items = items; }
            public Task<IReadOnlyList<MenuItem>> LoadAsync(string key, CancellationToken token) => Task.FromResult(items);
        }

        private sealed class FakeStore : IMenuUserStore
        {
            public readonly MenuUserPreferences State = new MenuUserPreferences { UserId = 1 };
            public readonly List<string> Calls = new List<string>();
            public Task<MenuUserPreferences> LoadAsync(string key, CancellationToken token) => Task.FromResult(State);
            public Task SetFavouriteAsync(string key, int menuItemId, bool isFavourite, int? sortOrder, CancellationToken token)
            {
                Calls.Add(menuItemId + ":" + isFavourite);
                if (isFavourite)
                {
                    State.Favorites.Add(menuItemId);
                    State.FavoriteOrder[menuItemId] = sortOrder ?? (State.FavoriteOrder.Count == 0 ? 1 : State.FavoriteOrder.Values.Max() + 1);
                }
                else { State.Favorites.Remove(menuItemId); State.FavoriteOrder.Remove(menuItemId); }
                return Task.FromResult(0);
            }
            public Task<DateTime> RecordUsageAsync(string key, int menuItemId, MenuLaunchMode mode, CancellationToken token)
            {
                var used = DateTime.Now;
                State.LastUsed[menuItemId] = used;
                return Task.FromResult(used);
            }
            public Task SetSettingAsync(string key, string name, string value, CancellationToken token)
            {
                Calls.Add("setting:" + name + "=" + value);
                State.Settings[name] = value;
                return Task.FromResult(0);
            }
        }

        private sealed class FakeEditor : IMenuEditor
        {
            public readonly List<string> Calls = new List<string>();
            public Task<int> SaveAsync(string key, MenuItem item, CancellationToken token)
            {
                Calls.Add("save:" + item.Id);
                return Task.FromResult(item.Id == 0 ? 99 : item.Id);
            }
            public Task RetireAsync(string key, int id, int? moveTo, CancellationToken token)
            {
                Calls.Add("retire:" + id + ":" + (moveTo.HasValue ? moveTo.Value.ToString() : "-"));
                return Task.FromResult(0);
            }
            public Task SetCategoryAsync(string key, int itemId, int categoryId, int? sortOrder, CancellationToken token)
            {
                Calls.Add("place:" + itemId + ":" + categoryId);
                return Task.FromResult(0);
            }
            public Task RemoveCategoryAsync(string key, int itemId, int categoryId, CancellationToken token)
            {
                Calls.Add("unplace:" + itemId + ":" + categoryId);
                return Task.FromResult(0);
            }
            public Task MoveCategoryAsync(string key, int categoryId, int delta, CancellationToken token)
            {
                Calls.Add("movecat:" + categoryId + ":" + delta);
                return Task.FromResult(0);
            }
            public Task MoveInCategoryAsync(string key, int itemId, int categoryId, int delta, CancellationToken token)
            {
                Calls.Add("moveitem:" + itemId + ":" + categoryId + ":" + delta);
                return Task.FromResult(0);
            }
        }

        public class TestForm : Form { public TestForm() { Text = "Test"; ShowInTaskbar = false; } }
        public class TestControl : UserControl { }

        #endregion
    }
}
