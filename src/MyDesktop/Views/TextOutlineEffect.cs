using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MyDesktop.Views;

/// <summary>
/// 文字描边：加在不透明的黑色文字副本上，画出八个方向各偏 Offset 个物理像素、每层不透明度为 Strength 的描边（着色器见 Assets/Shaders/TextOutline.fx）。
/// 效果等同于八份半透明副本叠在一起，但只多一份副本：每份副本约占 9 KB，八份的话 300 个图标要多 20 MB 以上。
/// </summary>
internal sealed class TextOutlineEffect : ShaderEffect
{
	static readonly PixelShader Shader = new() { UriSource = new Uri("pack://application:,,,/MyDesktop;component/Assets/Shaders/TextOutline.ps") };

	public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(TextOutlineEffect), 0);

	public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(TextOutlineEffect),
			new UIPropertyMetadata(0.25, PixelShaderConstantCallback(1)));

	public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(nameof(Offset), typeof(double), typeof(TextOutlineEffect),
			new UIPropertyMetadata(1.0, PixelShaderConstantCallback(2)));

	public TextOutlineEffect()
	{
		PixelShader = Shader;
		// 第 0 个常量寄存器由 WPF 填入纹理坐标对屏幕像素的偏导，着色器据此换算一个像素的偏移
		DdxUvDdyUvRegisterIndex = 0;
		// 描边超出文字本身的范围，四周各留 2
		PaddingLeft = 2;
		PaddingTop = 2;
		PaddingRight = 2;
		PaddingBottom = 2;
		UpdateShaderValue(InputProperty);
		UpdateShaderValue(StrengthProperty);
		UpdateShaderValue(OffsetProperty);
	}

	public Brush Input
	{
		get => (Brush)GetValue(InputProperty);
		set => SetValue(InputProperty, value);
	}

	public double Strength
	{
		get => (double)GetValue(StrengthProperty);
		set => SetValue(StrengthProperty, value);
	}

	public double Offset
	{
		get => (double)GetValue(OffsetProperty);
		set => SetValue(OffsetProperty, value);
	}
}
