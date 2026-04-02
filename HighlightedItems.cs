using System.Windows.Forms;
using HighlightedItems.Utils;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore.Shared.Enums;
using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ItemFilterLibrary;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace HighlightedItems;

public class HighlightedItems : BaseSettingsPlugin<Settings>
{
    private SyncTask<bool> _currentOperation;
    private string _customItemFilter = "";
    private int? _editFilterIndex;
    private string _editQuery = "";
    private string _editName = "";
    private string _editFolder = "";
    private string _saveAsQuerySnapshot = "";
    private string _saveAsName = "";
    private string _saveAsFolder = "";
    private string _folderDeleteKey;
    private string _folderDeleteLabel;
    private bool _openFolderDeleteConfirm;

    private record QueryOrException(ItemQuery Query, Exception Exception);

    private readonly ConditionalWeakTable<string, QueryOrException> _queries = [];

    private bool MoveCancellationRequested => Settings.CancelWithRightMouseButton && (Control.MouseButtons & MouseButtons.Right) != 0;
    private IngameState InGameState => GameController.IngameState;
    private SharpDX.Vector2 WindowOffset => GameController.Window.GetWindowRectangleTimeCache.TopLeft;

    public override bool Initialise()
    {
        MigrateLegacySavedFilters();
        Graphics.InitImage(Path.Combine(DirectoryFullName, "images\\pick.png").Replace('\\', '/'), false);
        Graphics.InitImage(Path.Combine(DirectoryFullName, "images\\pickL.png").Replace('\\', '/'), false);

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _mouseStateForRect.Clear();
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        DrawIgnoredCellsSettings();
    }

    private void MigrateLegacySavedFilters()
    {
        Settings.SavedFilters ??= [];
        Settings.SavedFilterEntries ??= [];
        if (Settings.SavedFilters.Count == 0)
            return;
        foreach (var q in Settings.SavedFilters)
        {
            if (string.IsNullOrEmpty(q))
                continue;
            if (Settings.SavedFilterEntries.Any(e => e.Query == q))
                continue;
            var name = q.Length > 48 ? q.Substring(0, 45) + "…" : q;
            Settings.SavedFilterEntries.Add(new SavedFilter { DisplayName = name, Query = q, Folder = "" });
        }
        Settings.SavedFilters.Clear();
    }

    private bool TryGetFilterAnchor(out Vector2 anchor)
    {
        anchor = default;
        var ui = InGameState.IngameUi;
        if (ui.StashElement is { IsVisible: true, VisibleStash: { InventoryUIElement: { } inv } })
        {
            anchor = inv.GetClientRectCache.BottomLeft.ToVector2Num();
            return true;
        }
        if (ui.GuildStashElement is { IsVisible: true, VisibleStash: { InventoryUIElement: { } inv2 } })
        {
            anchor = inv2.GetClientRectCache.BottomLeft.ToVector2Num();
            return true;
        }
        if (ui.InventoryPanel.IsVisible)
        {
            anchor = ui.InventoryPanel[2].GetClientRectCache.BottomLeft.ToVector2Num();
            return true;
        }
        if (ui.TradeWindow is { IsVisible: true })
        {
            anchor = ui.TradeWindow.GetClientRectCache.BottomLeft.ToVector2Num();
            return true;
        }
        if (ui.SellWindow is { IsVisible: true })
        {
            anchor = ui.SellWindow.GetClientRectCache.BottomLeft.ToVector2Num();
            return true;
        }
        return false;
    }

    private static string TruncateUi(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text.Substring(0, maxLen - 1) + "…";
    }

    private static string LineLabel(SavedFilter f) =>
        string.IsNullOrEmpty(f.DisplayName) ? TruncateUi(f.Query, 36) : TruncateUi(f.DisplayName, 44);

    private void RemoveFiltersInFolder(string folderKey)
    {
        var key = folderKey ?? "";
        Settings.SavedFilterEntries.RemoveAll(x => (x.Folder ?? "") == key);
    }

    private Predicate<Entity> GetPredicate(ref string filterText, Vector2 defaultPosition)
    {
        if (!Settings.ShowCustomFilterWindow)
            return null;
        Settings.SavedFilters ??= [];
        Settings.SavedFilterEntries ??= [];
        ImGui.SetNextWindowSizeConstraints(new Vector2(340, 200), new Vector2(900, 1200));
        ImGui.SetNextWindowSize(new Vector2(440, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(defaultPosition, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Custom filter", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
            return null;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 5));
        MigrateLegacySavedFilters();
        ImGui.TextUnformatted("Active IFL filter");
        ImGui.InputTextMultiline("##input", ref filterText, 8000, new Vector2(-1, 150));
        Predicate<Entity> returnValue = null;
        if (ImGui.Button("Clear"))
            filterText = "";
        ImGui.SameLine();
        var trimmed = filterText.Trim();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(trimmed));
        if (ImGui.Button("Save"))
        {
            _saveAsQuerySnapshot = trimmed;
            _saveAsName = trimmed.Length > 48 ? trimmed.Substring(0, 45) + "…" : trimmed;
            _saveAsFolder = "";
            ImGui.OpenPopup("hi_save_active_filter");
        }
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("hi_save_active_filter"))
        {
            ImGui.TextUnformatted("Name");
            ImGui.InputTextWithHint("##saveasname", "Name", ref _saveAsName, 128);
            ImGui.TextUnformatted("Group");
            ImGui.InputTextWithHint("##saveasfolder", "Group", ref _saveAsFolder, 64);
            if (ImGui.Button("Save"))
            {
                var q = _saveAsQuerySnapshot;
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var dn = string.IsNullOrWhiteSpace(_saveAsName.Trim()) ? TruncateUi(q, 48) : _saveAsName.Trim();
                    Settings.SavedFilterEntries.Add(new SavedFilter { DisplayName = dn, Query = q.Trim(), Folder = _saveAsFolder ?? "" });
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (!string.IsNullOrEmpty(trimmed))
        {
            var (query, exception) = _queries.GetValue(trimmed, s =>
            {
                try
                {
                    var itemQuery = ItemQuery.Load(s);
                    if (itemQuery.FailedToCompile)
                        return new QueryOrException(null, new Exception(itemQuery.Error));
                    return new QueryOrException(itemQuery, null);
                }
                catch (Exception ex)
                {
                    return new QueryOrException(null, ex);
                }
            })!;
            if (exception != null)
                ImGui.TextUnformatted($"{exception.Message}");
            else
                returnValue = s =>
                {
                    try
                    {
                        return query.CompiledQuery(new ItemData(s, GameController));
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.LogError($"Failed to match item: {ex}");
                        return false;
                    }
                };
        }
        ImGui.Separator();
        if (Settings.UsePopupForFilterSelector)
        {
            if (ImGui.Button("Open Saved Filters"))
                ImGui.OpenPopup("saved_filter_popup");
            if (ImGui.BeginPopup("saved_filter_popup"))
            {
                if (!Settings.SavedFilterEntries.Any())
                    ImGui.TextUnformatted("No saved filters yet.");
                else
                    DrawSavedFilterRows(ref filterText);
                ImGui.EndPopup();
            }
        }
        // ReSharper disable once AssignmentInConditionalExpression
        else if (Settings.OpenSavedFilterList = ImGui.TreeNodeEx("Saved filters",
                     Settings.OpenSavedFilterList
                         ? ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.NoTreePushOnOpen
                         : ImGuiTreeNodeFlags.NoTreePushOnOpen))
        {
            if (!Settings.SavedFilterEntries.Any())
                ImGui.TextUnformatted("No saved filters yet.");
            else
            {
                var savedH = Math.Max(120f, ImGui.GetContentRegionAvail().Y);
                ImGui.BeginChild("##saved_scroll", new Vector2(0, savedH), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
                DrawSavedFilterRows(ref filterText);
                ImGui.EndChild();
            }
            ImGui.TreePop();
        }
        if (_openFolderDeleteConfirm)
        {
            ImGui.OpenPopup("hi_confirm_folder_delete");
            _openFolderDeleteConfirm = false;
        }
        if (ImGui.BeginPopup("hi_confirm_folder_delete"))
        {
            ImGui.TextUnformatted($"Remove all filters in \"{_folderDeleteLabel}\"?");
            if (ImGui.Button("Remove all"))
            {
                RemoveFiltersInFolder(_folderDeleteKey);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar(2);
        ImGui.End();
        return returnValue;
    }

    private void DrawSavedFilterRows(ref string filterText)
    {
        ImGui.TextDisabled("Hover a name for full IFL; hold Shift and click Delete to remove a filter.");
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        var list = Settings.SavedFilterEntries;
        foreach (var folderGroup in list.Select((f, i) => (f, i)).GroupBy(t => t.f.Folder ?? "").OrderBy(g => g.Key))
        {
            var label = string.IsNullOrEmpty(folderGroup.Key) ? "General" : folderGroup.Key;
            ImGui.PushID($"fld_{label}");
            var folderId = $"##fld_{folderGroup.Key.GetHashCode():X8}";
            var treeOpen = ImGui.TreeNodeEx(folderId, ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(0, 8f);
            ImGui.PushID("foldtools");
            if (ImGui.Button("Delete group"))
            {
                _folderDeleteKey = folderGroup.Key;
                _folderDeleteLabel = label;
                _openFolderDeleteConfirm = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove every filter in this folder");
            ImGui.PopID();
            if (treeOpen)
            {
                ImGui.Separator();
                foreach (var (savedFilter, index) in folderGroup.OrderBy(x => x.i))
                {
                    ImGui.PushID($"saved{index}");
                    ImGui.Button("≡");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Drag to reorder");
                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
                    {
                        ImGuiHelpers.SetDragDropPayload("HI_FILTER_ITEM", index);
                        ImGui.EndDragDropSource();
                    }
                    if (ImGui.BeginDragDropTarget())
                    {
                        var payload = ImGuiHelpers.AcceptDragDropPayload<int>("HI_FILTER_ITEM");
                        if (payload.HasValue)
                        {
                            var srcIndex = payload.Value;
                            if (srcIndex >= 0 && srcIndex < list.Count && srcIndex != index)
                            {
                                var item = list[srcIndex];
                                list.RemoveAt(srcIndex);
                                var insertIndex = index;
                                if (insertIndex > srcIndex)
                                    insertIndex--;
                                list.Insert(insertIndex, item);
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Load"))
                        filterText = savedFilter.Query;
                    ImGui.SameLine();
                    if (ImGui.Button("Edit"))
                    {
                        _editFilterIndex = index;
                        _editQuery = savedFilter.Query;
                        _editName = savedFilter.DisplayName;
                        _editFolder = savedFilter.Folder ?? "";
                        ImGui.OpenPopup($"hi_edit_{index}");
                    }
                    if (ImGui.BeginPopup($"hi_edit_{index}"))
                    {
                        ImGui.TextUnformatted("Display name");
                        ImGui.InputTextWithHint("##editname", "Short name", ref _editName, 128);
                        ImGui.TextUnformatted("Folder");
                        ImGui.InputTextWithHint("##editfolder", "Optional group", ref _editFolder, 64);
                        ImGui.TextUnformatted("IFL query");
                        ImGui.InputTextMultiline("##editquery", ref _editQuery, 8000, new Vector2(-1, 150));
                        if (ImGui.Button("Save"))
                        {
                            savedFilter.Query = _editQuery.Trim();
                            savedFilter.DisplayName = _editName;
                            savedFilter.Folder = _editFolder ?? "";
                            ImGui.CloseCurrentPopup();
                            _editFilterIndex = null;
                        }
                        ImGui.EndPopup();
                    }
                    else if (_editFilterIndex == index)
                        _editFilterIndex = null;
                    ImGui.SameLine();
                    if (ImGui.Button("Delete"))
                    {
                        if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                            list.Remove(savedFilter);
                    }
                    else if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Hold Shift");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(LineLabel(savedFilter));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 42);
                        ImGui.TextUnformatted(savedFilter.Query);
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                    }
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.PopStyleVar(2);
    }

    public override void Render()
    {
        if (_currentOperation != null)
        {
            DebugWindow.LogMsg("Running the inventory dump procedure...");
            TaskUtils.RunOrRestart(ref _currentOperation, () => null);
            if (_itemsToMove is { Count: > 0 } itemsToMove)
            {
                foreach (var (rect, color) in itemsToMove.Skip(1).Select(x => (x, Settings.CustomFilterFrameColor)).Prepend((itemsToMove[0], Color.Green)))
                {
                    Graphics.DrawFrame(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(), color, Settings.CustomFilterFrameThickness);
                }
            }
            return;
        }

        if (!Settings.Enable)
            return;

        var panelsOpen = TryGetFilterAnchor(out var filterAnchor);
        if (Settings.ResetCustomFilterOnPanelClose && !panelsOpen)
            _customItemFilter = "";

        Predicate<Entity> entityPredicate = null;
        if (panelsOpen && Settings.ShowCustomFilterWindow)
            entityPredicate = GetPredicate(ref _customItemFilter, filterAnchor);

        var (inventory, rectElement) = (InGameState.IngameUi.StashElement, InGameState.IngameUi.GuildStashElement) switch
        {
            ({ IsVisible: true, VisibleStash: { InventoryUIElement: { } invRect } visibleStash }, _) => (visibleStash, invRect),
            (_, { IsVisible: true, VisibleStash: { InventoryUIElement: { } invRect } visibleStash }) => (visibleStash, invRect),
            _ => (null, null)
        };

        const float buttonSize = 37;
        var highlightedItemsFound = false;
        if (inventory != null)
        {
            var stashRect = rectElement.GetClientRectCache;
            var (itemFilter, isCustomFilter) = entityPredicate is { } customPredicate
                ? ((Predicate<NormalInventoryItem>)(s => customPredicate(s.Item)), true)
                : (s => s.isHighlighted != Settings.InvertSelection.Value, false);

            //Determine Stash Pickup Button position and draw
            var buttonPos = Settings.UseCustomMoveToInventoryButtonPosition
                ? Settings.CustomMoveToInventoryButtonPosition
                : stashRect.BottomRight.ToVector2Num() + new Vector2(-43, 10);
            var buttonRect = new SharpDX.RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);

            Graphics.DrawImage("pick.png", buttonRect);

            var highlightedItems = GetHighlightedItems(inventory, itemFilter);
            highlightedItemsFound = highlightedItems.Any();
            int? stackSizes = 0;
            foreach (var item in highlightedItems)
            {
                stackSizes += item.Item?.GetComponent<Stack>()?.Size;
                if (isCustomFilter)
                {
                    var rect = item.GetClientRectCache;
                    var deflateFactor = Settings.CustomFilterBorderDeflation / 200.0;
                    var deflateWidth = (int)(rect.Width * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                    var deflateHeight = (int)(rect.Height * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                    rect.Inflate(-deflateWidth, -deflateHeight);

                    var topLeft = rect.TopLeft.ToVector2Num();
                    var bottomRight = rect.BottomRight.ToVector2Num();
                    Graphics.DrawFrame(topLeft, bottomRight, Settings.CustomFilterFrameColor, Settings.CustomFilterBorderRounding, Settings.CustomFilterFrameThickness, 0);
                }
            }

            var countText = Settings.ShowStackSizes && highlightedItems.Count != stackSizes && stackSizes != null
                ? Settings.ShowStackCountWithSize
                    ? $"{stackSizes} / {highlightedItems.Count}"
                    : $"{stackSizes}"
                : $"{highlightedItems.Count}";

            var countPos = new Vector2(buttonRect.Left - 2, buttonRect.Center.Y - 11);
            Graphics.DrawText($"{countText}", countPos with { Y = countPos.Y + 2 }, SharpDX.Color.Black, FontAlign.Right);
            Graphics.DrawText($"{countText}", countPos with { X = countPos.X - 2 }, SharpDX.Color.White, FontAlign.Right);

            if (IsButtonPressed(buttonRect) ||
                Input.IsKeyDown(Settings.MoveToInventoryHotkey.Value))
            {
                var orderedItems = highlightedItems
                    .OrderBy(stashItem => stashItem.GetClientRectCache.X)
                    .ThenBy(stashItem => stashItem.GetClientRectCache.Y)
                    .ToList();
                _currentOperation = MoveItemsToInventory(orderedItems);
            }
        }

        var inventoryPanel = InGameState.IngameUi.InventoryPanel;
        if (inventoryPanel.IsVisible)
        {
            var inventoryRect = inventoryPanel[2].GetClientRectCache;

            var (itemFilter, isCustomFilter) = entityPredicate is { } customPredicate
                ? (customPredicate, true)
                : (_ => true, false);

            if (Settings.DumpButtonEnable && IsStashTargetOpened)
            {
                //Determine Inventory Pickup Button position and draw
                var buttonPos = Settings.UseCustomMoveToStashButtonPosition
                    ? Settings.CustomMoveToStashButtonPosition
                    : inventoryRect.TopLeft.ToVector2Num() + new Vector2(buttonSize / 2, -buttonSize);
                var buttonRect = new SharpDX.RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);

                if (isCustomFilter)
                {
                    foreach (var item in GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems.Where(x => itemFilter(x.Item)))
                    {
                        var rect = item.GetClientRect();
                        var deflateFactor = Settings.CustomFilterBorderDeflation / 200.0;
                        var deflateWidth = (int)(rect.Width * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                        var deflateHeight = (int)(rect.Height * deflateFactor + Settings.CustomFilterFrameThickness / 2);
                        rect.Inflate(-deflateWidth, -deflateHeight);

                        var topLeft = rect.TopLeft.ToVector2Num();
                        var bottomRight = rect.BottomRight.ToVector2Num();
                        Graphics.DrawFrame(topLeft, bottomRight, Settings.CustomFilterFrameColor, Settings.CustomFilterBorderRounding, Settings.CustomFilterFrameThickness, 0);
                    }
                }

                Graphics.DrawImage("pickL.png", buttonRect);
                if (IsButtonPressed(buttonRect) ||
                    Input.IsKeyDown(Settings.MoveToStashHotkey.Value) ||
                    Settings.UseMoveToInventoryAsMoveToStashWhenNoHighlights &&
                    !highlightedItemsFound &&
                    Input.IsKeyDown(Settings.MoveToInventoryHotkey.Value))
                {
                    var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems
                        .Where(x => !IsInIgnoreCell(x))
                        .Where(x => itemFilter(x.Item))
                        .OrderBy(x => x.PosX)
                        .ThenBy(x => x.PosY)
                        .ToList();

                    _currentOperation = MoveItemsToStash(inventoryItems);
                }
            }
        }
    }

    private async SyncTask<bool> MoveItemsCommonPreamble()
    {
        while (Control.MouseButtons == MouseButtons.Left || MoveCancellationRequested)
        {
            if (MoveCancellationRequested)
            {
                return false;
            }

            await TaskUtils.NextFrame();
        }

        if (Settings.IdleMouseDelay.Value == 0)
        {
            return true;
        }

        var mousePos = Mouse.GetCursorPosition();
        var sw = Stopwatch.StartNew();
        await TaskUtils.NextFrame();
        while (true)
        {
            if (MoveCancellationRequested)
            {
                return false;
            }

            var newPos = Mouse.GetCursorPosition();
            if (mousePos != newPos)
            {
                mousePos = newPos;
                sw.Restart();
            }
            else if (sw.ElapsedMilliseconds >= Settings.IdleMouseDelay.Value)
            {
                return true;
            }
            else
            {
                await TaskUtils.NextFrame();
            }
        }
    }

    private async SyncTask<bool> MoveItemsToStash(List<ServerInventory.InventSlotItem> items)
    {
        if (!await MoveItemsCommonPreamble())
        {
            return false;
        }

        _prevMousePos = Mouse.GetCursorPosition();
        Keyboard.KeyDown(Keys.LControlKey);
        await Wait(KeyDelay, true);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            _itemsToMove = items[i..].Select(x => x.GetClientRect()).ToList();
            if (MoveCancellationRequested) 
            {
                await StopMovingItems();
                return false;
            }

            if (!InGameState.IngameUi.InventoryPanel.IsVisible)
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory Panel closed, aborting loop");
                break;
            }

            if (!IsStashTargetOpened)
            {
                DebugWindow.LogMsg("HighlightedItems: Target inventory closed, aborting loop");
                break;
            }

            await MoveItem(item.GetClientRect().Center);
        }

        await StopMovingItems();
        return true;
    }

    private bool IsStashTargetOpened =>
        !Settings.VerifyTargetInventoryIsOpened
        || InGameState.IngameUi.StashElement.IsVisible
        || InGameState.IngameUi.SellWindow.IsVisible
        || InGameState.IngameUi.TradeWindow.IsVisible
        || InGameState.IngameUi.GuildStashElement.IsVisible;

    private bool IsStashSourceOpened =>
        !Settings.VerifyTargetInventoryIsOpened
        || InGameState.IngameUi.StashElement.IsVisible
        || InGameState.IngameUi.GuildStashElement.IsVisible;

    private List<RectangleF> _itemsToMove = null;
    private Point _prevMousePos = Point.Zero;

    private async SyncTask<bool> MoveItemsToInventory(List<NormalInventoryItem> items)
    {
        if (!await MoveItemsCommonPreamble())
        {
            return false;
        }

        _prevMousePos = Mouse.GetCursorPosition();
        Keyboard.KeyDown(Keys.LControlKey);
        await Wait(KeyDelay, true);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            _itemsToMove = items[i..].Select(x => x.GetClientRectCache).ToList();
            if (MoveCancellationRequested)
            {
                await StopMovingItems();
                return false;
            }

            if (!IsStashSourceOpened)
            {
                DebugWindow.LogMsg("HighlightedItems: Stash Panel closed, aborting loop");
                break;
            }

            if (!InGameState.IngameUi.InventoryPanel.IsVisible)
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory Panel closed, aborting loop");
                break;
            }

            if (IsInventoryFull())
            {
                DebugWindow.LogMsg("HighlightedItems: Inventory full, aborting loop");
                break;
            }

            await MoveItem(item.GetClientRect().Center);
        }

        await StopMovingItems();
        return true;
    }

    private async SyncTask<bool> StopMovingItems() {
        Keyboard.KeyUp(Keys.LControlKey);
        await Wait(KeyDelay, false);
        Mouse.moveMouse(_prevMousePos);
        _prevMousePos = Point.Zero;
        _itemsToMove = null;
        DebugWindow.LogMsg("HighlightedItems: Stopped moving items");
        return true;
    }

    private List<NormalInventoryItem> GetHighlightedItems(Inventory stash, Predicate<NormalInventoryItem> filter)
    {
        try
        {
            var stashItems = stash.VisibleInventoryItems;

            var highlightedItems = stashItems
                .Where(stashItem => filter(stashItem))
                .ToList();

            return highlightedItems;
        }
        catch
        {
            return [];
        }
    }

    private bool IsInventoryFull()
    {
        var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems;

        // quick sanity check
        if (inventoryItems.Count < 12)
        {
            return false;
        }

        // track each inventory slot
        bool[,] inventorySlot = new bool[12, 5];

        // iterate through each item in the inventory and mark used slots
        foreach (var inventoryItem in inventoryItems)
        {
            int x = inventoryItem.PosX;
            int y = inventoryItem.PosY;
            int height = inventoryItem.SizeY;
            int width = inventoryItem.SizeX;
            for (int row = x; row < x + width; row++)
            {
                for (int col = y; col < y + height; col++)
                {
                    inventorySlot[row, col] = true;
                }
            }
        }

        // check for any empty slots
        for (int x = 0; x < 12; x++)
        {
            for (int y = 0; y < 5; y++)
            {
                if (inventorySlot[x, y] == false)
                {
                    return false;
                }
            }
        }

        // no empty slots, so inventory is full
        return true;
    }

    private static readonly TimeSpan KeyDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MouseMoveDelay = TimeSpan.FromMilliseconds(20);
    private TimeSpan MouseDownDelay => TimeSpan.FromMilliseconds(25 + Settings.ExtraDelay.Value);
    private static readonly TimeSpan MouseUpDelay = TimeSpan.FromMilliseconds(5);

    private async SyncTask<bool> MoveItem(SharpDX.Vector2 itemPosition)
    {
        itemPosition += WindowOffset;
        Mouse.moveMouse(itemPosition);
        await Wait(MouseMoveDelay, true);
        Mouse.LeftDown();
        await Wait(MouseDownDelay, true);
        Mouse.LeftUp();
        await Wait(MouseUpDelay, true);
        return true;
    }

    private async SyncTask<bool> Wait(TimeSpan period, bool canUseThreadSleep)
    {
        if (canUseThreadSleep && Settings.UseThreadSleep)
        {
            Thread.Sleep(period);
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < period)
        {
            await TaskUtils.NextFrame();
        }

        return true;
    }

    private readonly ConcurrentDictionary<RectangleF, bool?> _mouseStateForRect = [];

    private bool IsButtonPressed(RectangleF buttonRect)
    {
        var prevState = _mouseStateForRect.GetValueOrDefault(buttonRect);
        var isHovered = buttonRect.Contains(Mouse.GetCursorPosition() - WindowOffset);
        if (!isHovered)
        {
            _mouseStateForRect[buttonRect] = null;
            return false;
        }

        var isPressed = Control.MouseButtons == MouseButtons.Left && CanClickButtons;
        _mouseStateForRect[buttonRect] = isPressed;
        return isPressed &&
               prevState == false;
    }

    private bool CanClickButtons => !Settings.VerifyButtonIsNotObstructed || !ImGui.GetIO().WantCaptureMouse;

    private bool IsInIgnoreCell(ServerInventory.InventSlotItem inventItem)
    {
        var inventPosX = inventItem.PosX;
        var inventPosY = inventItem.PosY;

        if (inventPosX < 0 || inventPosX >= 12)
            return true;
        if (inventPosY < 0 || inventPosY >= 5)
            return true;

        return Settings.IgnoredCells[inventPosY, inventPosX]; //No need to check all item size
    }

    private void DrawIgnoredCellsSettings()
    {
        ImGui.BeginChild("##IgnoredCellsMain", new Vector2(ImGui.GetContentRegionAvail().X, 204f), ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.Text("Ignored Inventory Slots (checked = ignored)");

        var contentRegionAvail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("##IgnoredCellsCels", new Vector2(contentRegionAvail.X, contentRegionAvail.Y), ImGuiChildFlags.Border,
            ImGuiWindowFlags.NoScrollWithMouse);

        for (int y = 0; y < 5; ++y)
        {
            for (int x = 0; x < 12; ++x)
            {
                bool isCellIgnored = Settings.IgnoredCells[y, x];
                if (ImGui.Checkbox($"##{y}_{x}IgnoredCells", ref isCellIgnored))
                    Settings.IgnoredCells[y, x] = isCellIgnored;
                if (x < 11)
                    ImGui.SameLine();
            }
        }

        ImGui.EndChild();
        ImGui.EndChild();
    }
}
