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
      new FrameworkPropertyMetadata(InheritsJob, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty StrictAnglesProperty = DependencyProperty.Register(
      nameof(StrictAngles),
      typeof(AnglesEnum),
      typeof(RotationSelector),
      new FrameworkPropertyMetadata(AnglesEnum.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    // The seven orientation permissions (photo IMG_1722), with an arrow icon language: ALL of
    // them are arrows — a single up arrow (only 0°), a single side arrow (only 90°), a bent
    // quarter-turn arrow (0+90), ↔ (0+180), ↕ (90+270), a four-way cross — and a circle for "any".
    // Codes are RotationCodes' permitted-orientation codes.
    private enum IconKind
    {
      ArrowUp,
      ArrowRight,
      QuarterTurn,
      ArrowH,
      ArrowV,
      FourArrows,
      AnyCircle,
      Inherit,
    }

    /// <summary>The part has chosen nothing and follows the job. Anything above zero is a choice, which
    /// is what the engine tests, so this has to be the same sentinel the model was born with.</summary>
    internal const int InheritsJob = -1;

    private static readonly (string Label, int Rotations, IconKind Icon, AnglesEnum Strict, string Tip)[] Options = new[]
    {
      ("Job default", InheritsJob, IconKind.Inherit, AnglesEnum.None, "Whatever the job is set to, under Settings > Advanced Settings. Change it there and this part follows."),
      ("As drawn", 1, IconKind.ArrowUp, AnglesEnum.None, "Only 0°, so the part stays exactly as drawn (respects grain)."),
      ("90° only", RasterNest.RotationCodes.RotOnly90, IconKind.ArrowRight, AnglesEnum.None, "Only 90°, so the part is always turned once."),
      ("0°+90°", RasterNest.RotationCodes.RotZeroAnd90, IconKind.QuarterTurn, AnglesEnum.None, "0° and 90° permitted."),
      ("0°+180°", 2, IconKind.ArrowH, AnglesEnum.AsPreviewed, "0° and 180°: respects material grain but can flip."),
      ("90°+270°", RasterNest.RotationCodes.Rot90And270, IconKind.ArrowV, AnglesEnum.None, "90° and 270°: always turned, either way."),
      ("4-way", 4, IconKind.FourArrows, AnglesEnum.None, "All four square orientations (0 / 90 / 180 / 270°). The safe first choice on square and L shaped parts: it usually beats free rotation on those, and always costs less to search."),
      ("45° steps", 8, IconKind.FourArrows, AnglesEnum.None, "Eight orientations, 45° apart. The job setting offers this too, and without it here a part could not be shown what it was really set to."),
      ("Free", 36, IconKind.AnyCircle, AnglesEnum.None, "Any angle, and NOT automatically the tightest. It wins on parts with slanted or curved edges, and loses on square or L shaped ones, which interlock exactly at right angles and gain nothing from being tilted. Measured on two real jobs: 84.3% of the block filled against 4-way's 81.6% on one, and 69.6% against 73.2% on the other. Costs about twice the search. Ignores grain."),
    };

    private static readonly Brush IconFill = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x80)); // classic navy

    private readonly List<ToggleButton> buttons = new List<ToggleButton>();

    public RotationSelector()
    {
      InitializeComponent();

      if (IconFill.CanFreeze)
      {
        IconFill.Freeze();
      }

      for (int i = 0; i < Options.Length; i++)
      {
        var opt = Options[i];
        var btn = new ToggleButton
        {
          Style = (Style)this.Resources["RotationOptionStyle"],
          ToolTip = BuildTip(opt.Tip),
          Tag = i,
          Content = BuildContent(opt.Label, opt.Icon),
        };
        ToolTipService.SetShowDuration(btn, 30000);
        ToolTipService.SetInitialShowDelay(btn, 300);
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

      // An exact match is handled above. What is left is a code no option represents, and the honest
      // answer is that the part follows the job rather than a shape that is not what it says. Showing
      // 45 degree steps as "Free" is how a user came to believe free rotation was broken: the picker
      // said free, the parts list said free, and the engine was handed eight discrete angles.
      return 0; // Job default
    }

    /// <summary>A tooltip that WRAPS. The default renders a string on one line however long it is, so
    /// anything past a short phrase comes out as a box wider than the screen, which is no tooltip at all.
    /// These say what each option costs and when it wins, so they are sentences, not phrases.</summary>
    private static ToolTip BuildTip(string text)
      => new ToolTip
      {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 },
      };

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
        case IconKind.ArrowUp:
          canvas.Children.Add(Shaft(12, 17, 12, 8));
          canvas.Children.Add(Head(new Point(12, 5), -90));
          break;

        case IconKind.ArrowRight:
          canvas.Children.Add(Shaft(7, 12, 16, 12));
          canvas.Children.Add(Head(new Point(19, 12), 0));
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

        // Dashed, because this part has chosen nothing: the shape comes from the job.
        case IconKind.Inherit:
          var dashed = new Ellipse
          {
            Width = 15,
            Height = 15,
            Stroke = IconFill,
            StrokeThickness = 1.8,
            StrokeDashArray = new DoubleCollection(new[] { 2.0, 2.0 }),
          };
          Canvas.SetLeft(dashed, 4.5);
          Canvas.SetTop(dashed, 4.5);
          canvas.Children.Add(dashed);
          break;
      }

      return canvas;
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
