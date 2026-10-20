using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AlpTogether.Effects
{
    public class VideoColorEnhanceEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(VideoColorEnhanceEffect), 0);

        public static readonly DependencyProperty BrightnessProperty =
            DependencyProperty.Register(
                nameof(Brightness),
                typeof(double),
                typeof(VideoColorEnhanceEffect),
                new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty ContrastProperty =
            DependencyProperty.Register(
                nameof(Contrast),
                typeof(double),
                typeof(VideoColorEnhanceEffect),
                new UIPropertyMetadata(1.15, PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty SaturationProperty =
            DependencyProperty.Register(
                nameof(Saturation),
                typeof(double),
                typeof(VideoColorEnhanceEffect),
                new UIPropertyMetadata(1.30, PixelShaderConstantCallback(2)));

        public static readonly DependencyProperty GammaProperty =
            DependencyProperty.Register(
                nameof(Gamma),
                typeof(double),
                typeof(VideoColorEnhanceEffect),
                new UIPropertyMetadata(0.95, PixelShaderConstantCallback(3)));

        private static readonly PixelShader _pixelShader = new()
        {
            UriSource = new Uri("pack://application:,,,/AlpTogether;component/Effects/VideoColorEnhanceEffect.ps", UriKind.Absolute)
        };

        public VideoColorEnhanceEffect()
        {
            PixelShader = _pixelShader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(BrightnessProperty);
            UpdateShaderValue(ContrastProperty);
            UpdateShaderValue(SaturationProperty);
            UpdateShaderValue(GammaProperty);
        }

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public double Brightness
        {
            get => (double)GetValue(BrightnessProperty);
            set => SetValue(BrightnessProperty, value);
        }

        public double Contrast
        {
            get => (double)GetValue(ContrastProperty);
            set => SetValue(ContrastProperty, value);
        }

        public double Saturation
        {
            get => (double)GetValue(SaturationProperty);
            set => SetValue(SaturationProperty, value);
        }

        public double Gamma
        {
            get => (double)GetValue(GammaProperty);
            set => SetValue(GammaProperty, value);
        }
    }
}
