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

    private static readonly (string Label, int Rotations, AnglesEnum Strict, string Tip)[] Options = new[]
    {
      ("No turn", 1, AnglesEnum.None, "No rotation — parts stay exactly as drawn (respects grain)."),
      ("Flip", 2, AnglesEnum.AsPreviewed, "Allow 0° and 180° only — respects material grain but can flip."),
      ("90°", 4, AnglesEnum.None, "Allow 0 / 90 / 180 / 270°."),
      ("Free", 36, AnglesEnum.None, "Any angle (best fit, ignores grain)."),
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
          Content = BuildContent(opt.Label, opt.Rotations),
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

    /// <summary>Maps the current Rotations + StrictAngles back to one of the four options.</summary>
    private int SelectedIndex()
    {
      if (this.StrictAngles == AnglesEnum.AsPreviewed)
      {
        return 1; // Flip
      }

      if (this.Rotations <= 1)
      {
        return 0; // No turn
      }

      if (this.Rotations <= 4)
      {
        return 2; // 90°
      }

      return 3; // Free
    }

    private static FrameworkElement BuildContent(string label, int rotations)
    {
      var panel = new StackPanel { Orientation = Orientation.Vertical };
      panel.Children.Add(BuildIcon(rotations));
      panel.Children.Add(new TextBlock
      {
        Text = label,
        FontSize = 9.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
      });
      return panel;
    }

    private static UIElement BuildIcon(int rotations)
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

      if (rotations >= 36)
      {
        canvas.Children.Add(BuildCurvedArrow(c, r));
      }
      else
      {
        int n = Math.Max(1, rotations);
        for (int i = 0; i < n; i++)
        {
          double angDeg = -90 + (i * (360.0 / n));
          canvas.Children.Add(BuildArrowhead(c, r, angDeg));
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
