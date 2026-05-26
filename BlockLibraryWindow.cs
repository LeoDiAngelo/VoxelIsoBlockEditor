using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsoBlockCharacterEditor;

public sealed class BlockLibraryWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly VoxelEditorControl _preview = new();
    private readonly ObservableCollection<BlockPiece> _previewPieces = new();
    private readonly HashSet<BlockPiece> _previewSelection = new();

    public event Action<BlockShape>? ShapeChosen;

    public BlockLibraryWindow(BlockShape current)
    {
        Title = "Voxel Iso Block Editor - Block Shape Library";
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(14, 18, 24));

        // Important: mark this VoxelEditorControl as a library-preview instance
        // before WpfGame.Initialize can run. Otherwise Initialize() may call
        // CenterOnGrid() later and overwrite the preview camera/orthographic zoom.
        _preview.ConfigureAsLibraryPreviewControl();

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(left, 0);
        root.Children.Add(left);
        var heading = new TextBlock { Text = "Block shapes", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        _list.ItemsSource = BlockCatalog.Definitions;
        _list.Background = new SolidColorBrush(Color.FromRgb(18, 25, 35));
        _list.Foreground = Brushes.White;
        _list.BorderBrush = new SolidColorBrush(Color.FromRgb(43, 52, 68));
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _list.MouseDoubleClick += (_, _) => ChooseSelected();
        left.Children.Add(_list);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 13, 18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 52, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6)
        };
        Grid.SetRow(previewBorder, 0);
        right.Children.Add(previewBorder);
        previewBorder.Child = _preview;

        var info = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(info, 1);
        right.Children.Add(info);
        _title.Foreground = Brushes.White;
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.Bold;
        info.Children.Add(_title);
        _description.Foreground = new SolidColorBrush(Color.FromRgb(190, 202, 220));
        _description.TextWrapping = TextWrapping.Wrap;
        _description.Margin = new Thickness(0, 4, 0, 8);
        info.Children.Add(_description);
        info.Children.Add(new TextBlock { Text = "Preview controls: right/middle drag to rotate, mouse wheel to zoom.", Foreground = new SolidColorBrush(Color.FromRgb(145, 158, 178)), Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Close", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var choose = new Button { Content = "Use selected block", Width = 150 };
        choose.Click += (_, _) => ChooseSelected();
        buttons.Children.Add(cancel); buttons.Children.Add(choose); info.Children.Add(buttons);

        Loaded += (_, _) =>
        {
            _preview.AttachData(_previewPieces, _previewSelection);
            _preview.GridSize = 1;
            _preview.ShowFloorGrid = true;
            _preview.ShowLeftGrid = false;
            _preview.ShowRightGrid = false;
            UpdatePreview();
            _preview.CenterOnPreviewBlock();
            _preview.InvalidateVisualScene();

            // Run once more after layout/render initialization. WpfGame can finish
            // its GraphicsDevice setup after Window.Loaded, and this guarantees the
            // library starts with the intended preview zoom instead of the main-grid camera.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _preview.CenterOnPreviewBlock();
                _preview.InvalidateVisualScene();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };

        BlockDefinition? selectedDefinition = null;
        for (int i = 0; i < BlockCatalog.Definitions.Length; i++)
        {
            BlockDefinition definition = BlockCatalog.Definitions[i];
            if (definition.Shape == current)
            {
                selectedDefinition = definition;
                break;
            }
        }

        _list.SelectedItem = selectedDefinition ?? BlockCatalog.Definitions[0];
    }

    private void ChooseSelected()
    {
        if (_list.SelectedItem is BlockDefinition def)
            ShapeChosen?.Invoke(def.Shape);
    }

    private void UpdatePreview()
    {
        if (_list.SelectedItem is not BlockDefinition def) return;
        _title.Text = def.Name;
        _description.Text = def.Description;
        _previewPieces.Clear();
        var piece = new BlockPiece
        {
            X = 0,
            Y = 0,
            Z = 0,
            Shape = def.Shape,
            ColorHex = "#4B88D8",
            RotationY = def.LibraryPreviewRotationY
        };
        _previewPieces.Add(piece);
        _previewSelection.Clear();
        _previewSelection.Add(piece);
        _preview.InvalidateVisualScene();
        // Do not re-center every time the selected shape changes. The preview
        // camera uses a fixed canonical 1x1 framing and should stay stable so
        // all shapes keep the same size and user rotation/zoom does not jump.
    }
}
