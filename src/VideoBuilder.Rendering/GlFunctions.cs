using System.Runtime.InteropServices;
using System.Text;

namespace VideoBuilder.Rendering;

/// <summary>
/// Skia を通さずに使う OpenGL の関数 (動画のフレームを YUV のまま送って GPU で色を変換する・描いた絵を待たずに読み戻す)。
/// Skia と同じ取り出し方 (GpuContext の getProcAddress) で関数を探す。OpenGL ES 3.0 以上か、デスクトップの OpenGL 3.0 以上でなければ作らない。
/// デスクトップの OpenGL は、Linux で画面の描画 (Avalonia の GLX) と共有するコンテキストのとき (設計書 22.28)。
/// 使う関数はどちらにもあるので同じで、違うのはシェーダの書き方 (<see cref="ShaderVersion"/>) だけ。
/// 使ったあとは GRContext.ResetContext で Skia に GL の状態が変わったことを知らせること。
/// </summary>
internal sealed unsafe class GlFunctions
{
    public const uint TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, UNSIGNED_BYTE = 0x1401;
    public const uint R8 = 0x8229, RED = 0x1903, RG8 = 0x822B, RG = 0x8227, RGBA8 = 0x8058, RGBA = 0x1908;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int NEAREST = 0x2600, LINEAR = 0x2601, CLAMP_TO_EDGE = 0x812F;
    public const uint UNPACK_ROW_LENGTH = 0x0CF2, UNPACK_SKIP_ROWS = 0x0CF3, UNPACK_SKIP_PIXELS = 0x0CF4, UNPACK_ALIGNMENT = 0x0CF5;
    public const uint PACK_ROW_LENGTH = 0x0D02, PACK_SKIP_ROWS = 0x0D03, PACK_SKIP_PIXELS = 0x0D04, PACK_ALIGNMENT = 0x0D05;
    public const uint PIXEL_PACK_BUFFER = 0x88EB, PIXEL_UNPACK_BUFFER = 0x88EC, STREAM_READ = 0x88E1, MAP_READ_BIT = 0x0001;
    public const uint FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const uint TRIANGLE_STRIP = 0x0005;
    public const uint BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11, DEPTH_TEST = 0x0B71, STENCIL_TEST = 0x0B90, CULL_FACE = 0x0B44, DITHER = 0x0BD0;
    public const uint VERSION = 0x1F02;
    public const uint SYNC_GPU_COMMANDS_COMPLETE = 0x9117, SYNC_FLUSH_COMMANDS_BIT = 0x0001;
    public const uint ALREADY_SIGNALED = 0x911A, CONDITION_SATISFIED = 0x911C, TIMEOUT_EXPIRED = 0x911B, WAIT_FAILED = 0x911D;

    public delegate* unmanaged<uint, byte*> GetString;
    public delegate* unmanaged<uint> GetError;
    public delegate* unmanaged<int, uint*, void> GenTextures, DeleteTextures, GenFramebuffers, DeleteFramebuffers, GenVertexArrays, DeleteVertexArrays, GenBuffers, DeleteBuffers;
    public delegate* unmanaged<uint, uint, void> BindTexture, BindFramebuffer, BindBuffer, AttachShader;
    public delegate* unmanaged<uint, void> ActiveTexture, CompileShader, DeleteShader, LinkProgram, UseProgram, DeleteProgram, BindVertexArray, Disable;
    public delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexSubImage2D;
    public delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public delegate* unmanaged<uint, int, void> PixelStorei;
    public delegate* unmanaged<uint, uint> CreateShader;
    public delegate* unmanaged<uint> CreateProgram;
    public delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog, GetProgramInfoLog;
    public delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public delegate* unmanaged<int, int, void> Uniform1i;
    public delegate* unmanaged<int, int, int, void> Uniform2i;
    public delegate* unmanaged<int, float, float, void> Uniform2f;
    public delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public delegate* unmanaged<int, int, byte, float*, void> UniformMatrix3fv;
    public delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public delegate* unmanaged<int, int, int, int, void> Viewport;
    public delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    public delegate* unmanaged<uint, int, int, void> DrawArrays;
    public delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    public delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public delegate* unmanaged<uint, nint, nint, uint, void*> MapBufferRange;
    public delegate* unmanaged<uint, byte> UnmapBuffer;
    public delegate* unmanaged<uint, uint, IntPtr> FenceSync;
    public delegate* unmanaged<IntPtr, uint, ulong, uint> ClientWaitSync;
    public delegate* unmanaged<IntPtr, void> DeleteSync;
    public delegate* unmanaged<void> Flush;

    /// <summary>OpenGL ES か (false ならデスクトップの OpenGL)。</summary>
    public bool IsGles { get; private set; }

    /// <summary>
    /// シェーダの先頭に付ける版の指定。OpenGL ES 3 は "#version 300 es"、デスクトップの OpenGL 3.3 以上は "#version 330 core"、
    /// 3.0〜3.2 は "#version 130"。シェーダの本文は、どれでも同じ意味になる書き方にする
    /// (precision の指定は GLSL 1.30 以降のデスクトップでも書けて、無視される)。
    /// </summary>
    public string ShaderVersion { get; private set; } = "#version 300 es";

    /// <summary>
    /// GL_VERSION の文字列から、OpenGL ES か・版 (major, minor) を読む。読めなければ null。
    /// 例: "OpenGL ES 3.2 Mesa 24.0.5" / "OpenGL ES 3.0 (ANGLE 2.1 ...)" / "4.6.0 NVIDIA 550.54.14" / "4.5 (Compatibility Profile) Mesa 24.0.5"。
    /// </summary>
    internal static (bool Gles, int Major, int Minor)? ParseVersion(string version)
    {
        const string es = "OpenGL ES ";
        bool gles = version.StartsWith(es, StringComparison.Ordinal);
        var rest = gles ? version[es.Length..] : version;
        int dot = rest.IndexOf('.');
        if (dot <= 0 || !int.TryParse(rest[..dot], out int major)) return null;
        int end = dot + 1;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        if (end == dot + 1 || !int.TryParse(rest[(dot + 1)..end], out int minor)) return null;
        return (gles, major, minor);
    }

    /// <summary>この版で使えるか (OpenGL ES 3.0 以上・デスクトップの OpenGL 3.0 以上)。使えるときはシェーダの版の指定も返す。</summary>
    internal static string? ShaderVersionFor(bool gles, int major, int minor) =>
        gles ? (major >= 3 ? "#version 300 es" : null)
             : major > 3 || major == 3 && minor >= 3 ? "#version 330 core" : major == 3 ? "#version 130" : null;

    /// <summary>関数を探す。OpenGL ES 3.0 以上・OpenGL 3.0 以上でない・関数が足りないときは null (error に理由)。今のスレッドでコンテキストが有効なこと。</summary>
    public static GlFunctions? Load(Func<string, IntPtr> proc, out string? error)
    {
        error = null;
        var gl = new GlFunctions();
        var missing = new List<string>();
        IntPtr P(string name)
        {
            var p = proc(name);
            if (p == IntPtr.Zero) missing.Add(name);
            return p;
        }
        gl.GetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        if (gl.GetString == null) { error = "glGetString がありません"; return null; }
        var version = Marshal.PtrToStringAnsi((IntPtr)gl.GetString(VERSION)) ?? "";
        var parsed = ParseVersion(version);
        var shaderVersion = parsed is var (gles, major, minor) ? ShaderVersionFor(gles, major, minor) : null;
        if (parsed == null || shaderVersion == null)
        {
            error = $"OpenGL ES 3 / OpenGL 3 ではありません ({version})";
            return null;
        }
        gl.IsGles = parsed.Value.Gles;
        gl.ShaderVersion = shaderVersion;
        gl.GetError = (delegate* unmanaged<uint>)P("glGetError");
        gl.GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        gl.DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        gl.GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        gl.DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        gl.GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        gl.DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        gl.GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        gl.DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        gl.BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        gl.BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        gl.BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        gl.AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        gl.ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        gl.CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        gl.DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        gl.LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        gl.UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        gl.DeleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
        gl.BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        gl.Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        gl.TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        gl.TexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexSubImage2D");
        gl.TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        gl.PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        gl.CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        gl.CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        gl.ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        gl.GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        gl.GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        gl.GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        gl.GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        gl.GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        gl.Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        gl.Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        gl.Uniform2i = (delegate* unmanaged<int, int, int, void>)P("glUniform2i");
        gl.Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        gl.UniformMatrix3fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix3fv");
        gl.FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        gl.CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        gl.Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        gl.ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        gl.DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        gl.BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        gl.ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        gl.MapBufferRange = (delegate* unmanaged<uint, nint, nint, uint, void*>)P("glMapBufferRange");
        gl.UnmapBuffer = (delegate* unmanaged<uint, byte>)P("glUnmapBuffer");
        gl.FenceSync = (delegate* unmanaged<uint, uint, IntPtr>)P("glFenceSync");
        gl.ClientWaitSync = (delegate* unmanaged<IntPtr, uint, ulong, uint>)P("glClientWaitSync");
        gl.DeleteSync = (delegate* unmanaged<IntPtr, void>)P("glDeleteSync");
        gl.Flush = (delegate* unmanaged<void>)P("glFlush");
        if (missing.Count > 0) { error = "関数がありません: " + string.Join(", ", missing); return null; }
        return gl;
    }

    /// <summary>頂点とフラグメントのシェーダから、プログラムを作る。失敗したら 0 (error に理由)。</summary>
    public uint Program(string vertex, string fragment, out string? error)
    {
        error = null;
        uint Compile(uint type, string source, out string? e)
        {
            e = null;
            uint shader = CreateShader(type);
            var bytes = Encoding.UTF8.GetBytes(source);
            fixed (byte* b = bytes)
            {
                byte* list = b;
                int length = bytes.Length;
                ShaderSource(shader, 1, &list, &length);
            }
            CompileShader(shader);
            int ok;
            GetShaderiv(shader, COMPILE_STATUS, &ok);
            if (ok != 0) return shader;
            var log = stackalloc byte[1024];
            int len;
            GetShaderInfoLog(shader, 1024, &len, log);
            e = Marshal.PtrToStringAnsi((IntPtr)log, Math.Clamp(len, 0, 1024));
            DeleteShader(shader);
            return 0;
        }
        uint vs = Compile(VERTEX_SHADER, vertex, out error);
        if (vs == 0) return 0;
        uint fs = Compile(FRAGMENT_SHADER, fragment, out error);
        if (fs == 0) { DeleteShader(vs); return 0; }
        uint program = CreateProgram();
        AttachShader(program, vs);
        AttachShader(program, fs);
        LinkProgram(program);
        DeleteShader(vs);
        DeleteShader(fs);
        int linked;
        GetProgramiv(program, LINK_STATUS, &linked);
        if (linked != 0) return program;
        var log = stackalloc byte[1024];
        int len;
        GetProgramInfoLog(program, 1024, &len, log);
        error = Marshal.PtrToStringAnsi((IntPtr)log, Math.Clamp(len, 0, 1024));
        DeleteProgram(program);
        return 0;
    }

    public int Uniform(uint program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* b = bytes) return GetUniformLocation(program, b);
    }

    public uint GenTexture() { uint t; GenTextures(1, &t); return t; }
    public void DeleteTexture(uint t) { if (t != 0) DeleteTextures(1, &t); }
}
