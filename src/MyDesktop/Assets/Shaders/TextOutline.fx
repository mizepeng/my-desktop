// 文字描边：把输入（不透明的黑色文字副本）向八个方向各挪 Offset 个像素叠起来，每层的不透明度为 Strength，
// 效果等同于八份半透明黑色副本依次叠加（1 - Π(1 - Strength × 覆盖度)），但只用一份副本。
// 修改后运行同目录的 Compile-Shaders.ps1 重新生成 TextOutline.ps。

sampler2D input : register(s0);

// WPF 填入：xy 为 uv 对 x 的偏导，zw 为 uv 对 y 的偏导，即中间纹理里一个像素对应的 uv 步长
float4 ddxDdy : register(c0);

// 每个方向的不透明度
float strength : register(c1);

// 偏移的像素数（中间纹理的像素，即物理像素）
float offset : register(c2);

float Layer(float2 uv)
{
	return 1 - strength * tex2D(input, uv).a;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
	float2 dx = ddxDdy.xy * offset;
	float2 dy = ddxDdy.zw * offset;
	float keep = Layer(uv - dx - dy) * Layer(uv - dy) * Layer(uv + dx - dy)
			* Layer(uv - dx) * Layer(uv + dx)
			* Layer(uv - dx + dy) * Layer(uv + dy) * Layer(uv + dx + dy);
	// 预乘透明度的黑色
	return float4(0, 0, 0, 1 - keep);
}
