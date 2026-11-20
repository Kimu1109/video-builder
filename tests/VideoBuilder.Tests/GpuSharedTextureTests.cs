using SkiaSharp;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>Windows: 画面の描画と共有する Direct3D 11 のテクスチャ (SharedD3DTexture)。CI では WARP で確かめる。</summary>
public class GpuSharedTextureTests
{
    [Fact]
    public void Shared_d3d_texture_can_be_drawn_with_skia_and_handed_over_with_the_keyed_mutex()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var gpu = GpuContext.TryCreate(out var error);
        Assert.True(gpu != null || !GpuSelectionTests.GpuRequired, error);
        if (gpu == null) return;
        Assert.NotEqual(0, gpu.DeviceLuid);

        gpu.MakeCurrent();
        using var texture = gpu.CreateSharedD3DTexture(64, 48, out error);
        Assert.True(texture != null, error);
        Assert.NotEqual(IntPtr.Zero, texture!.SharedHandle);
        Assert.NotEqual(0u, texture.GlTexture);

        // 描く側が鍵を取り、Skia で GL のテクスチャに描けて、描いた色が読める
        Assert.True(texture.AcquireForDrawing(1000));
        const int GL_TEXTURE_2D = 0x0DE1;
        var info = new GRGlTextureInfo(GL_TEXTURE_2D, texture.GlTexture, (uint)SharedD3DTexture.GlInternalFormat);
        using (var backend = new GRBackendTexture(64, 48, false, info))
        using (var surface = SKSurface.Create(gpu.Context, backend, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888))
        {
            Assert.NotNull(surface);
            surface!.Canvas.Clear(new SKColor(0x20, 0x80, 0xE0));
            surface.Canvas.Flush();
            gpu.Context.Flush(true, true);
            using var bmp = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
            Assert.True(surface.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0));
            var c = bmp.GetPixel(10, 10);
            Assert.Equal((0x20, 0x80, 0xE0), (c.Red, c.Green, c.Blue));
        }

        // 画面の描画に渡したら (鍵 1)、画面の描画が返す (鍵 0) までは描く側は取れない
        texture.ReleaseForPresent();
        Assert.False(texture.Held);
        Assert.False(texture.AcquireForDrawing(50));
    }
}
