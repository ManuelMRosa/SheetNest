namespace DeepNestSharp.Ui.UserControls
{
  using System;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Input;
  using System.Windows.Media;

  /// <summary>
  /// Nest report's page on screen, handled like the nest viewer so the two feel the same: the mouse wheel zooms
  /// toward the cursor, holding the wheel down pans, and a right-click fits the whole page back. The numbers are
  /// the viewer's (<see cref="DxfViewer"/>): 1.2 per notch, between 0.02 and 400, fitted at 92 % of the room.
  /// </summary>
  public partial class ReportPageView : UserControl
  {
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
      nameof(Source),
      typeof(ImageSource),
      typeof(ReportPageView),
      new PropertyMetadata(null, OnSourceChanged));

    // The page is the size the report prints it, said once in NestReportPdf.
    private const double PageW = DeepNestSharp.Reports.NestReportPdf.PageW;
    private const double PageH = DeepNestSharp.Reports.NestReportPdf.PageH;

    private bool fitted;
    private bool isPanning;
    private Point panStart;
    private double panTranslateX;
    private double panTranslateY;

    public ReportPageView()
    {
      InitializeComponent();

      this.page.Width = PageW;
      this.page.Height = PageH;

      // A page set while there was no room to fit it into (the report was still collapsed) is fitted as soon as
      // there is; after that the operator's zoom is left alone.
      this.host.SizeChanged += (s, e) =>
      {
        if (!this.fitted)
        {
          this.FitToView();
        }
      };
    }

    /// <summary>Gets or sets the page shown. A new page, another nest's, starts fitted.</summary>
    public ImageSource Source
    {
      get => (ImageSource)GetValue(SourceProperty);
      set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// The scale and translation after one wheel notch toward the cursor. The page point under the cursor stays
    /// under it: screen = page * scale + translate, before and after. The viewer's own rule, in one place that a
    /// test can reach.
    /// </summary>
    internal static (double Scale, double X, double Y) ZoomAt(double scale, double x, double y, double cursorX, double cursorY, int wheelDelta)
    {
      double newScale = Math.Max(0.02, Math.Min(400.0, scale * (wheelDelta > 0 ? 1.2 : 1.0 / 1.2)));
      double factor = newScale / scale;
      return (newScale, cursorX - ((cursorX - x) * factor), cursorY - ((cursorY - y) * factor));
    }

    /// <summary>The scale and translation that show the whole content centred in the room, filling 92 % of the
    /// tighter direction, as the viewer fits a sheet.</summary>
    internal static (double Scale, double X, double Y) Fit(double contentW, double contentH, double availW, double availH)
    {
      double s = Math.Min(availW / contentW, availH / contentH) * 0.92;
      return (s, (availW - (contentW * s)) / 2.0, (availH - (contentH * s)) / 2.0);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      var view = (ReportPageView)d;
      view.page.Source = (ImageSource)e.NewValue;

      // Fitted once layout has run, not now. The page is set just as the report is being shown, when the host still
      // reports the size it had the last time it was on screen: fitting to that framed the page for a window that had
      // since been resized, and the real size arriving afterwards was ignored as already fitted.
      view.fitted = false;
      view.Dispatcher.BeginInvoke(new Action(view.FitToView), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitToView()
    {
      double availW = this.host.ActualWidth;
      double availH = this.host.ActualHeight;
      if (availW <= 0 || availH <= 0)
      {
        return;
      }

      var (s, x, y) = Fit(PageW, PageH, availW, availH);
      this.Apply(s, x, y);
      this.fitted = true;
    }

    private void Apply(double s, double x, double y)
    {
      this.scale.ScaleX = s;
      this.scale.ScaleY = s;
      this.translate.X = x;
      this.translate.Y = y;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
      Point p = e.GetPosition(this.host);
      var (s, x, y) = ZoomAt(this.scale.ScaleX, this.translate.X, this.translate.Y, p.X, p.Y, e.Delta);
      this.Apply(s, x, y);
      e.Handled = true;
    }

    /// <summary>Holding the wheel down pans, as in the viewer; the left button does nothing here.</summary>
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (e.ChangedButton != MouseButton.Middle)
      {
        return;
      }

      this.host.Focus();
      this.isPanning = true;
      this.panStart = e.GetPosition(this.host);
      this.panTranslateX = this.translate.X;
      this.panTranslateY = this.translate.Y;
      this.host.CaptureMouse();
      this.host.Cursor = Cursors.SizeAll;
      e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
      if (!this.isPanning)
      {
        return;
      }

      Point p = e.GetPosition(this.host);
      this.translate.X = this.panTranslateX + (p.X - this.panStart.X);
      this.translate.Y = this.panTranslateY + (p.Y - this.panStart.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
      if (e.ChangedButton == MouseButton.Middle && this.isPanning)
      {
        this.isPanning = false;
        this.host.ReleaseMouseCapture();
        this.host.Cursor = Cursors.Arrow;
        e.Handled = true;
      }
    }

    private void OnResetView(object sender, MouseButtonEventArgs e)
    {
      this.FitToView();
      e.Handled = true;
    }
  }
}
