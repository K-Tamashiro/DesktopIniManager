using DesktopIniManager.Properties;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private readonly HashSet<int> selectedEditHunks = new();
    private bool savingText, confirmingTextChanges, allowEditedClose;

    private void AddTextSaveButton(TextBlock header, bool source)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = ViewModel.IsDirty(source),
            ToolTip = StringOverlay.Get(source ? "Diff_SaveSource" : "Diff_SaveTarget"),
            Content = new Image { Source = DifferencerStatusIcons.GetCustomIcon(59), Stretch = Stretch.Uniform }
        };
        button.Click += async (_, _) =>
        {
            clipboardSource = source;
            await SaveTextSideAsync(source);
        };
        header.Inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center });
    }

    private bool HandleTextEditKey(KeyEventArgs e)
    {
        if (!ViewModel.IsText || Keyboard.Modifiers != ModifierKeys.Control) return false;
        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                _ = SaveTextSideAsync(clipboardSource);
                return true;

            case Key.Z:
                e.Handled = true;
                if (!savingText)
                {
                    ViewModel.UndoEdit();
                    _ = RenderTextEditsAsync();
                }

                return true;

            case Key.A:
                e.Handled = true;
                selectedEditHunks.Clear();
                foreach (var row in lines.Where(r => r.EditHunkId >= 0)) selectedEditHunks.Add(row.EditHunkId);
                var pane = clipboardSource ? leftList : rightList;
                pane?.SelectAll();
                RefreshEditArrows();
                return true;
        }

        return false;
    }

    private void UpdateTextHunkSelection(RichTextBox pane)
    {
        if (renderingContent || savingText || pane != (clipboardSource ? leftList : rightList)) return;
        selectedEditHunks.Clear();
        if (!pane.Selection.IsEmpty)
        {
            var row = 0;
            foreach (var block in pane.Document.Blocks)
            {
                if (row >= lines.Count) break;
                if (block.ContentEnd.CompareTo(pane.Selection.Start) >= 0 &&
                    block.ContentStart.CompareTo(pane.Selection.End) < 0 && lines[row].EditHunkId >= 0)
                    selectedEditHunks.Add(lines[row].EditHunkId);
                row++;
            }
        }

        RefreshEditArrows();
    }

    private void BuildEditArrows(Canvas viewport, bool source)
    {
        while (viewport.Children.Count > 1) viewport.Children.RemoveAt(1);
        if (!ViewModel.IsText || lines == null) return;
        foreach (var row in hunks)
        {
            var id = lines[row].EditHunkId;
            if (id < 0) continue;
            var button = new Button
            {
                Width = 24,
                Height = DiffLineHeight,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Focusable = false,
                Tag = (row, id, source),
                Content = new Image { Stretch = Stretch.Uniform }
            };
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                if (renderingContent || savingText || !ViewModel.CanChangeComparison) return;
                clipboardSource = source;
                if (ViewModel.CanUndoHunk(id, source)) ViewModel.UndoHunk(id, source);
                else
                    ViewModel.CopyHunks(selectedEditHunks.Contains(id) ? selectedEditHunks.ToArray() : new[] { id },
                        source);
                await RenderTextEditsAsync();
            };
            viewport.Children.Add(button);
            Canvas.SetLeft(button, 0);
        }

        UpdateEditArrowAppearance(viewport);
        PositionEditArrows(viewport, -Canvas.GetTop(viewport.Children[0]));
    }

    private void UpdateEditArrowAppearance(Canvas viewport)
    {
        foreach (var button in viewport.Children.OfType<Button>())
        {
            var (row, id, source) = ((int, int, bool))button.Tag;
            var undo = ViewModel.CanUndoHunk(id, source);
            var first = selectedEditHunks.Where(i => !ViewModel.CanUndoHunk(i, source)).DefaultIfEmpty(-1).Min();
            var grouped = !undo && selectedEditHunks.Contains(id) && first != id;
            var icon = undo ? source ? 154 : 152 : grouped ? source ? 152 : 154 : source ? 153 : 151;
            ((Image)button.Content).Source = DifferencerStatusIcons.GetCustomIcon(icon);
            button.IsEnabled = !grouped;
            button.ToolTip =
                StringOverlay.Get(undo ? "Diff_UndoHunk" : source ? "Diff_CopyToTarget" : "Diff_CopyToSource");
        }
    }

    private void RefreshEditArrows()
    {
        if (leftList?.Tag is Canvas left) UpdateEditArrowAppearance(left);
        if (rightList?.Tag is Canvas right) UpdateEditArrowAppearance(right);
    }

    private static void PositionEditArrows(Canvas viewport, double offset)
    {
        foreach (var button in viewport.Children.OfType<Button>())
        {
            var (row, _, _) = ((int, int, bool))button.Tag;
            Canvas.SetTop(button, row * DiffLineHeight - offset);
        }
    }

    private async Task RenderTextEditsAsync()
    {
        var offset = (leftScroll ?? FindScroll(leftList))?.VerticalOffset ?? 0;
        await LoadContent(false, false);
        leftList?.UpdateLayout();
        leftScroll = FindScroll(leftList);
        rightScroll = FindScroll(rightList);
        leftScroll?.ScrollToVerticalOffset(offset);
        rightScroll?.ScrollToVerticalOffset(offset);
    }

    private async Task RecompareWorkingTextAsync()
    {
        if (renderingContent || savingText || !ViewModel.CanChangeComparison) return;
        if (!ViewModel.IsText)
        {
            await ViewModel.ReloadFromDiskAsync();
            return;
        }

        SetRendering(true);
        try
        {
            await ViewModel.RecompareTextAsync();
        }
        finally
        {
            SetRendering(false);
        }

        await LoadContent(false);
    }

    private async Task<bool> SaveTextSideAsync(bool source)
    {
        if (savingText || renderingContent || !ViewModel.CanChangeComparison) return false;
        if (!ViewModel.IsDirty(source)) return true;
        savingText = true;
        SetRendering(true);
        try
        {
            await ViewModel.SaveTextAsync(source);
        }
        catch (EncoderFallbackException ex)
        {
            await ShowEncodingErrorAsync(source, ex.Index);
            return false;
        }
        catch (DiffViewModel.UnrepresentableTextException ex)
        {
            await ShowEncodingErrorAsync(source, ex.Index);
            return false;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
            MessageBox.Show(this, ex.Message, StringOverlay.Get("Diff_SaveError"), MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        finally
        {
            savingText = false;
            SetRendering(false);
        }

        await LoadContent(false);
        return true;
    }

    private async Task ShowEncodingErrorAsync(bool source, int index)
    {
        var (line, column) = ViewModel.EncodingErrorPosition(source, Math.Max(0, index));
        MessageBox.Show(this, string.Format(StringOverlay.Get("Diff_EncodingError"),
                source ? "Source" : "Target", (source ? ViewModel.SourceText : ViewModel.TargetText).EncodingName, line,
                column + 1),
            StringOverlay.Get("Diff_SaveError"), MessageBoxButton.OK, MessageBoxImage.Warning);
        if (ViewModel.DifferencesOnly) ViewModel.ToggleDifferencesOnly();
        SetRendering(false);
        await LoadContent(false, false);
        var row = lines.FindIndex(r => (source ? r.LeftNumber : r.RightNumber) == line);
        if (row < 0) return;
        var pane = source ? leftList : rightList;
        pane.Focus();
        clipboardSource = source;
        ScrollPaneToLine(pane, row);
        var paragraph = pane.Document.Blocks.ElementAt(row) as Paragraph;
        if (paragraph == null) return;
        // Rendered tabs occupy four columns, so map the original column to display text.
        var raw = (source ? ViewModel.SourceText : ViewModel.TargetText).Lines[line - 1];
        var displayColumn = raw[..Math.Min(column, raw.Length)].Replace("\t", "    ").Length;
        var start = TextPosition(paragraph, displayColumn);
        var end = TextPosition(paragraph,
            displayColumn + (column < raw.Length && char.IsHighSurrogate(raw[column]) ? 2 : 1));
        pane.Selection.Select(start, end);
    }

    private static TextPointer TextPosition(Paragraph paragraph, int offset)
    {
        var position = paragraph.ContentStart;
        while (position != null && position.CompareTo(paragraph.ContentEnd) < 0)
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var count = position.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= count) return position.GetPositionAtOffset(offset);
                offset -= count;
                position = position.GetPositionAtOffset(count);
            }
            else
            {
                position = position.GetNextContextPosition(LogicalDirection.Forward);
            }

        return paragraph.ContentEnd;
    }

    private async Task<bool> ConfirmTextChangesAsync()
    {
        if (confirmingTextChanges || savingText) return false;
        if (!ViewModel.HasEdits) return true;
        confirmingTextChanges = true;
        try
        {
            var dialog = new Window
            {
                Owner = this,
                Title = StringOverlay.Get("Diff_UnsavedTitle"),
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            { Text = StringOverlay.Get("Diff_UnsavedPrompt"), Margin = new Thickness(0, 0, 0, 20) });
            var buttons = new StackPanel
            { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var choice = 0;
            foreach (var item in new[] { ("Diff_Save", 1), ("Diff_Discard", 2), ("Diff_Cancel", 0) })
            {
                var button = new Button
                {
                    Content = StringOverlay.Get(item.Item1),
                    MinWidth = 90,
                    Margin = new Thickness(5),
                    Padding = new Thickness(10, 5, 10, 5),
                    IsCancel = item.Item2 == 0
                };
                button.Click += (_, _) =>
                {
                    choice = item.Item2;
                    dialog.DialogResult = choice != 0;
                };
                buttons.Children.Add(button);
            }

            panel.Children.Add(buttons);
            dialog.Content = panel;
            if (dialog.ShowDialog() != true) return false;
            if (choice == 2) return true;
            return await SaveTextSideAsync(true) && await SaveTextSideAsync(false);
        }
        finally
        {
            confirmingTextChanges = false;
        }
    }
}