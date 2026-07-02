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

    // The seven Radan orientation permissions (photo IMG_1722): only 0° / only 90° / 0+90 / 0+180 /
    // 90+270 / all four / any. Codes are RasterNestService's permitted-orientation codes; Angles is
    // the icon's arrowhead set (null = any → curved arrow).
    private static readonly (string Label, int Rotations, int[] Angles, AnglesEnum Strict, string Tip)[] Options = new[]
    {
      ("As drawn", 1, new[] { 0 }, AnglesEnum.None, "Only 0° — the part stays exactly as drawn (respects grain)."),
      ("90° only", RasterNest.RasterNestService.RotOnly90, new[] { 90 }, AnglesEnum.None, "Only 90° — the part is always turned once."),
      ("0°+90°", RasterNest.RasterNestService.RotZeroAnd90, new[] { 0, 90 }, AnglesEnum.None, "0° and 90° permitted."),
      ("Flip", 2, new[] { 0, 180 }, AnglesEnum.AsPreviewed, "0° and 180° — respects material grain but can flip."),
      ("90°+270°", RasterNest.RasterNestService.Rot90And270, new[] { 90, 270 }, AnglesEnum.None, "90° and 270° — always turned, either way."),
      ("4-way", 4, new[] { 0, 90, 180, 270 }, AnglesEnum.None, "All four square orientations (0 / 90 / 180 / 270°)."),
      ("Free", 36, null, AnglesEnum.None, "Any angle (best fit, ignores grain)."),
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
          Content = BuildContent(opt.Label, opt.Angles),
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

    private static FrameworkElement BuildContent(string label, int[] angles)
    {
      var panel = new StackPanel { Orientation = Orientation.Vertical };
      panel.Children.Add(BuildIcon(angles));
      panel.Children.Add(new TextBlock
      {
        Text = label,
        FontSize = 9.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
      });
      return panel;
    }

    private static UIElement BuildIcon(int[] angles)
    {
      const double size = 24;
      const double c = size / 2;
      const double r = 8;
      var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };

      var circle = new Ellipse
      {
        Width = 2 * r,
        Height = 2 * r,
        Stroke = IconStroke,
        StrokeThickness = 1.4,
      };
      Canvas.SetLeft(circle, c - r);
      Canvas.SetTop(circle, c - r);
      canvas.Children.Add(circle);

      var dot = new Ellipse { Width = 2.0, Height = 2.0, Fill = IconStroke };
      Canvas.SetLeft(dot, c - 1.0);
      Canvas.SetTop(dot, c - 1.0);
      canvas.Children.Add(dot);

      if (angles == null)
      {
        canvas.Children.Add(BuildCurvedArrow(c, r));
      }
      else
      {
        foreach (int angle in angles)
        {
          canvas.Children.Add(BuildArrowhead(c, r, angle - 90));
        }
      }

      return canvas;
    }

    private static Polygon BuildArrowhead(double c, double r, double angleDeg)
    {
      double a = angleDeg * Math.PI / 180.0;
      double dx = Math.Cos(a);
      double dy = Math.Sin(a);
      double px = -dy;
      double py = dx;

      double tipR = r + 3.0;
      double baseR = r - 0.5;
      const double halfW = 2.2;

      var tip = new Point(c + (tipR * dx), c + (tipR * dy));
      var b1 = new Point(c + (baseR * dx) + (halfW * px), c + (baseR * dy) + (halfW * py));
      var b2 = new Point(c + (baseR * dx) - (halfW * px), c + (baseR * dy) - (halfW * py));

      return new Polygon
      {
        Points = new PointCollection { tip, b1, b2 },
        Fill = IconFill,
      };
    }

    private static Canvas BuildCurvedArrow(double c, double r)
    {
      double ar = r + 1.0;
      double startDeg = -60;
      double endDeg = 230;
      Point Pt(double deg) => new Point(c + (ar * Math.Cos(deg * Math.PI / 180.0)), c + (ar * Math.Sin(deg * Math.PI / 180.0)));

      var fig = new PathFigure { StartPoint = Pt(startDeg), IsClosed = false };
      fig.Segments.Add(new ArcSegment(Pt(endDeg), new Size(ar, ar), 0, true, SweepDirection.Clockwise, true));
      var geo = new PathGeometry();
      geo.Figures.Add(fig);

      var path = new Path { Data = geo, Stroke = IconFill, StrokeThickness = 1.6 };

      var holder = new Canvas();
      holder.Children.Add(path);
      double tan = endDeg + 90;
      double a = tan * Math.PI / 180.0;
      var end = Pt(endDeg);
      double dx = Math.Cos(a);
      double dy = Math.Sin(a);
      double px = -dy;
      double py = dx;
      var tip = new Point(end.X + (4 * dx), end.Y + (4 * dy));
      var b1 = new Point(end.X + (3 * px), end.Y + (3 * py));
      var b2 = new Point(end.X - (3 * px), end.Y - (3 * py));
      holder.Children.Add(new Polygon { Points = new PointCollection { tip, b1, b2 }, Fill = IconFill });
      return holder;
    }
  }
}
