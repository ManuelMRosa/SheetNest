namespace DeepNestSharp.Ui.UserControls
{
  using System;
  using System.Collections.Generic;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Controls.Primitives;
  using System.Windows.Media;
  using System.Windows.Shapes;
  using DeepNestLib.NestProject;

  /// <summary>
  /// Single visual rotation picker (replaces the separate Rotations + grain controls). Each option
  /// sets BOTH the engine's <see cref="Rotations"/> step count and <see cref="StrictAngles"/> grain
  /// rule, drawn as a circle with arrows at the allowed orientations.
  /// </summary>
  public partial class RotationSelector : UserControl
  {
    public static readonly DependencyProperty RotationsProperty = DependencyProperty.Register(
      nameof(Rotations),
      typeof(int),
      typeof(RotationSelector),
      new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty StrictAnglesProperty = DependencyProperty.Register(
      nameof(StrictAngles),
      typeof(AnglesEnum),
      typeof(RotationSelector),
      new FrameworkPropertyMetadata(AnglesEnum.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    // The seven Radan orientation permissions (photo IMG_1722), with Radan's icon language: the part
    // shape fixed / the part turned / a quarter-turn arrow / ↔ / ↕ / four-way arrows / a circle.
    // Codes are RasterNestService's permitted-orientation codes.
    private enum IconKind
    {
      PartUpright,
      PartTurned,
      QuarterTurn,
      ArrowH,
      ArrowV,
      FourArrows,
      AnyCircle,
    }

    private static readonly (string Label, int Rotations, IconKind Icon, AnglesEnum Strict, string Tip)[] Options = new[]
    {
      ("As drawn", 1, IconKind.PartUpright, AnglesEnum.None, "Only 0° — the part stays exactly as drawn (respects grain)."),
      ("90° only", RasterNest.RasterNestService.RotOnly90, IconKind.PartTurned, AnglesEnum.None, "Only 90° — the part is always turned once."),
      ("0°+90°", RasterNest.RasterNestService.RotZeroAnd90, IconKind.QuarterTurn, AnglesEnum.None, "0° and 90° permitted."),
      ("0°+180°", 2, IconKind.ArrowH, AnglesEnum.AsPreviewed, "0° and 180° — respects material grain but can flip."),
      ("90°+270°", RasterNest.RasterNestService.Rot90And270, IconKind.ArrowV, AnglesEnum.None, "90° and 270° — always turned, either way."),
      ("4-way", 4, IconKind.FourArrows, AnglesEnum.None, "All four square orientations (0 / 90 / 180 / 270°)."),
      ("Free", 36, IconKind.AnyCircle, AnglesEnum.None, "Any angle (best fit, ignores grain)."),
    };

    private static readonly Brush IconStroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush IconFill = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));

    private readonly List<ToggleButton> buttons = new List<ToggleButton>();

    public RotationSelector()
    {
      InitializeComponent();

      if (IconStroke.CanFreeze)
      {
        IconStroke.Freeze();
        IconFill.Freeze();
      }

      for (int i = 0; i < Options.Length; i++)
      {
        var opt = Options[i];
        var btn = new ToggleButton
        {
          Style = (Style)this.Resources["RotationOptionStyle"],
          ToolTip = opt.Tip,
          Tag = i,
          Content = BuildContent(opt.Label, opt.Icon),
        };
        btn.Click += this.OnOptionClick;
        this.buttons.Add(btn);
        this.optionsPanel.Children.Add(btn);
      }

      this.UpdateChecked();
    }

    public int Rotations
    {
      get => (int)this.GetValue(RotationsProperty);
      set => this.SetValue(RotationsProperty, value);
    }

    public AnglesEnum StrictAngles
    {
      get => (AnglesEnum)this.GetValue(StrictAnglesProperty);
      set => this.SetValue(StrictAnglesProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      ((RotationSelector)d).UpdateChecked();
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
      var opt = Options[(int)((ToggleButton)sender).Tag];
      this.Rotations = opt.Rotations;
      this.StrictAngles = opt.Strict;
      this.UpdateChecked();
    }

    private void UpdateChecked()
    {
      int selected = this.SelectedIndex();
      for (int i = 0; i < this.buttons.Count; i++)
      {
        this.buttons[i].IsChecked = i == selected;
      }
    }

    /// <summary>Maps the current Rotations code back to one of the seven options (legacy values —
    /// e.g. a persisted 8 = 45° steps — snap to the closest option).</summary>
    private int SelectedIndex()
    {
      for (int i = 0; i < Options.Length; i++)
      {
        if (Options[i].Rotations == this.Rotations)
        {
          return i;
        }
      }

      if (this.Rotations <= 1)
      {
        return 0; // As drawn
      }

      if (this.Rotations <= 4)
      {
        return 5; // 4-way
      }

      return Options.Length - 1; // Free
    }

    private static FrameworkElement BuildContent(string label, IconKind icon)
    {
      var panel = new StackPanel { Orientation = Orientation.Vertical };
      panel.Children.Add(BuildIcon(icon));
      panel.Children.Add(new TextBlock
      {
        Text = label,
        FontSize = 9.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
      });
      return panel;
    }

    private static UIElement BuildIcon(IconKind kind)
    {
      const double size = 24;
      var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };

      switch (kind)
      {
        case IconKind.PartUpright:
          canvas.Children.Add(LShape(turned: false));
          break;

        case IconKind.PartTurned:
          canvas.Children.Add(LShape(turned: true));
          break;

        case IconKind.QuarterTurn:
          // Bent arrow: up then right — a quarter turn.
          canvas.Children.Add(Shaft(8, 18, 8, 10));
          canvas.Children.Add(Shaft(7.1, 10, 14, 10));
          canvas.Children.Add(Head(new Point(18, 10), 0));
          break;

        case IconKind.ArrowH:
          canvas.Children.Add(Shaft(7, 12, 17, 12));
          canvas.Children.Add(Head(new Point(19, 12), 0));
          canvas.Children.Add(Head(new Point(5, 12), 180));
          break;

        case IconKind.ArrowV:
          canvas.Children.Add(Shaft(12, 7, 12, 17));
          canvas.Children.Add(Head(new Point(12, 5), -90));
          canvas.Children.Add(Head(new Point(12, 19), 90));
          break;

        case IconKind.FourArrows:
          canvas.Children.Add(Shaft(7, 12, 17, 12));
          canvas.Children.Add(Shaft(12, 7, 12, 17));
          canvas.Children.Add(Head(new Point(19, 12), 0));
          canvas.Children.Add(Head(new Point(5, 12), 180));
          canvas.Children.Add(Head(new Point(12, 5), -90));
          canvas.Children.Add(Head(new Point(12, 19), 90));
          break;

        case IconKind.AnyCircle:
          var circle = new Ellipse { Width = 15, Height = 15, Stroke = IconFill, StrokeThickness = 1.8 };
          Canvas.SetLeft(circle, 4.5);
          Canvas.SetTop(circle, 4.5);
          canvas.Children.Add(circle);
          break;
      }

      return canvas;
    }

    /// <summary>Small L-shaped part, upright or turned 90° clockwise — Radan's "the part, as placed" glyphs.</summary>
    private static Polygon LShape(bool turned)
    {
      var upright = new[] { (8.0, 5.0), (13.0, 5.0), (13.0, 12.0), (18.0, 12.0), (18.0, 19.0), (8.0, 19.0) };
      var pts = new PointCollection();
      foreach (var (x, y) in upright)
      {
        pts.Add(turned ? new Point(24 - y, x) : new Point(x, y));
      }

      return new Polygon
      {
        Points = pts,
        Stroke = IconStroke,
        StrokeThickness = 1.3,
        Fill = new SolidColorBrush(Color.FromRgb(0xDB, 0xEC, 0xDB)),
      };
    }

    private static Line Shaft(double x1, double y1, double x2, double y2)
    {
      return new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = IconFill, StrokeThickness = 1.8 };
    }

    private static Polygon Head(Point tip, double angleDeg)
    {
      const double len = 5.0;
      const double halfW = 2.8;
      double a = angleDeg * Math.PI / 180.0;
      double dx = Math.Cos(a);
      double dy = Math.Sin(a);
      double px = -dy;
      double py = dx;
      var b = new Point(tip.X - (len * dx), tip.Y - (len * dy));

      return new Polygon
      {
        Points = new PointCollection
        {
          tip,
          new Point(b.X + (halfW * px), b.Y + (halfW * py)),
          new Point(b.X - (halfW * px), b.Y - (halfW * py)),
        },
        Fill = IconFill,
      };
    }
  }
}
