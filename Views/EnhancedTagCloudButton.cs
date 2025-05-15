using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Enhanced TagCloudControl with improved visuals for standalone window
    /// </summary>
    public class EnhancedTagCloudButton : Button
    {
        // Add custom properties for more control over appearance
        public double InitialFontSize { get; set; }
        public string TagText { get; set; }
        public int Count { get; set; }

        public EnhancedTagCloudButton()
        {
            // Apply advanced styling
            this.Background = Brushes.Transparent;
            this.Foreground = Brushes.White;
            this.BorderThickness = new Thickness(0);
            this.Padding = new Thickness(8, 4,8,4);
            this.Margin = new Thickness(3);
            this.Cursor = Cursors.Hand;

            // Set corner radius via template
            ControlTemplate template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            border.AppendChild(contentPresenter);
            template.VisualTree = border;

            // Add triggers for mouse over and pressed states
            var mouseOverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(60, 100, 100, 240)), "border"));
            mouseOverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "border"));
            template.Triggers.Add(mouseOverTrigger);

            var pressedTrigger = new Trigger { Property = IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(0.95, 0.95)));
            pressedTrigger.Setters.Add(new Setter(RenderTransformOriginProperty, new Point(0.5, 0.5)));
            template.Triggers.Add(pressedTrigger);

            this.Template = template;
        }
    }
}