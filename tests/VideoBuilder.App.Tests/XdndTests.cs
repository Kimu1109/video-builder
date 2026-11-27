using System.Text;
using VideoBuilder.App.Platform;
using static VideoBuilder.App.Platform.XdndDropTarget;

namespace VideoBuilder.App.Tests;

/// <summary>
/// ほかのアプリからのドロップ (X11 の XDND)。ファイルマネージャの代わりに、XDND の手順どおりに動く
/// 「ドラッグ元」をこのテストの中で作り、本物の X サーバー上でやり取りする。
/// X サーバーがない環境 (DISPLAY が空) では何もしない。xvfb-run で実行すると確かめられる。
/// </summary>
public unsafe class XdndTests
{
    [Fact]
    public void Uri_lists_are_parsed_into_local_paths()
    {
        var files = ParseUriList("# comment\r\nfile:///home/user/My%20Video.mp4\r\nfile:///tmp/%E6%97%A5%E6%9C%AC.png\r\nhttps://example.com/x.mp4\r\n/plain/path.wav\n\0");
        Assert.Equal(new[] { "/home/user/My Video.mp4", "/tmp/日本.png", "/plain/path.wav" }, files);
    }

    /// <summary>テスト用のドラッグ元。</summary>
    private sealed class FakeSource : IDisposable
    {
        public readonly IntPtr Display, Root, Window;
        private readonly Atoms _a;
        public string Payload = "";

        public FakeSource()
        {
            Display = X.XOpenDisplay(IntPtr.Zero);
            Root = X.XDefaultRootWindow(Display);
            Window = X.XCreateSimpleWindow(Display, Root, 0, 0, 10, 10, 0, IntPtr.Zero, IntPtr.Zero);
            _a = new Atoms(Display);
        }

        public IntPtr Atom(string name) => X.XInternAtom(Display, name, 0);

        public IntPtr CreateAppWindow(int x, int y)
        {
            var w = X.XCreateSimpleWindow(Display, Root, x, y, 400, 300, 0, IntPtr.Zero, IntPtr.Zero);
            X.XMapWindow(Display, w);
            X.XSync(Display, 0);
            return w;
        }

        /// <summary>ドラッグ元がやるのと同じく、ウィンドウの XdndProxy を調べて送り先を決める。</summary>
        public IntPtr ProxyOf(IntPtr window)
        {
            if (X.XGetWindowProperty(Display, window, _a.XdndProxy, IntPtr.Zero, (IntPtr)1, 0, (IntPtr)0, out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
                return IntPtr.Zero;
            try { return format == 32 && count.ToInt64() == 1 ? *(IntPtr*)data : IntPtr.Zero; }
            finally { X.XFree(data); }
        }

        public void Send(IntPtr to, IntPtr target, IntPtr type, IntPtr l1 = default, IntPtr l2 = default, IntPtr l3 = default, IntPtr l4 = default)
        {
            var e = new XEvent();
            e.ClientMessage.type = 33; e.ClientMessage.window = target; e.ClientMessage.message_type = type; e.ClientMessage.format = 32;
            e.ClientMessage.l0 = Window; e.ClientMessage.l1 = l1; e.ClientMessage.l2 = l2; e.ClientMessage.l3 = l3; e.ClientMessage.l4 = l4;
            X.XSendEvent(Display, to, 0, IntPtr.Zero, ref e);
            X.XFlush(Display);
        }

        /// <summary>type のメッセージが届くまで待つ。途中でデータの要求が来たら、Payload を渡す。</summary>
        public XClientMessageEvent? WaitFor(IntPtr type, int timeoutMs = 3000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var e = new XEvent();
            while (DateTime.UtcNow < until)
            {
                if (X.XPending(Display) == 0) { Thread.Sleep(5); continue; }
                X.XNextEvent(Display, ref e);
                if (e.type == 30 /* SelectionRequest */) Answer(ref e.SelectionRequest);
                else if (e.type == 33 && e.ClientMessage.message_type == type) return e.ClientMessage;
            }
            return null;
        }

        private void Answer(ref XSelectionRequestEvent r)
        {
            var bytes = Encoding.UTF8.GetBytes(Payload);
            fixed (byte* p = bytes)
                X.XChangeProperty(Display, r.requestor, r.property, r.target, 8, 0, (IntPtr)p, bytes.Length);
            var n = new XEvent();
            n.Selection.type = 31; n.Selection.requestor = r.requestor; n.Selection.selection = r.selection;
            n.Selection.target = r.target; n.Selection.property = r.property; n.Selection.time = r.time;
            X.XSendEvent(Display, r.requestor, 0, IntPtr.Zero, ref n);
            X.XFlush(Display);
        }

        public void Dispose()
        {
            X.XDestroyWindow(Display, Window);
            X.XCloseDisplay(Display);
        }
    }

    [Fact]
    public void Files_dropped_from_another_application_are_received_with_their_position()
    {
        using var target = XdndDropTarget.TryCreate();
        if (target == null) return;     // X サーバーがない
        using var src = new FakeSource();
        var app = src.CreateAppWindow(100, 50);       // アプリのウィンドウの代わり
        var moved = new List<(int, int)>();
        (IntPtr Window, int X, int Y, IReadOnlyList<string> Files)? dropped = null;
        using var done = new ManualResetEventSlim();
        target.Moved += (w, x, y) => { lock (moved) moved.Add((x, y)); };
        target.Dropped += (w, x, y, f) => { dropped = (w, x, y, f); done.Set(); };
        target.Attach(app);
        Thread.Sleep(50);

        var proxy = src.ProxyOf(app);
        Assert.NotEqual(IntPtr.Zero, proxy);              // アプリのウィンドウには代理ウィンドウの目印が付いている
        Assert.NotEqual(app, proxy);

        IntPtr uriList = src.Atom("text/uri-list"), copy = src.Atom("XdndActionCopy");
        X.XSetSelectionOwner(src.Display, src.Atom("XdndSelection"), src.Window, IntPtr.Zero);
        src.Payload = "file:///home/user/clip%201.mp4\r\nfile:///home/user/%E7%94%BB%E5%83%8F.png\r\n";
        src.Send(proxy, app, src.Atom("XdndEnter"), (IntPtr)(5L << 24), uriList);
        src.Send(proxy, app, src.Atom("XdndPosition"), default, (IntPtr)((130L << 16) | 70), IntPtr.Zero, copy);
        var status = src.WaitFor(src.Atom("XdndStatus"));
        Assert.NotNull(status);
        Assert.Equal(1, status!.Value.l1.ToInt64() & 1);  // 受け付ける
        Assert.Equal(copy, status.Value.l4);

        src.Send(proxy, app, src.Atom("XdndPosition"), default, (IntPtr)((150L << 16) | 90), IntPtr.Zero, copy);
        src.WaitFor(src.Atom("XdndStatus"));
        src.Send(proxy, app, src.Atom("XdndDrop"), default, IntPtr.Zero);
        var finished = src.WaitFor(src.Atom("XdndFinished"));
        Assert.NotNull(finished);
        Assert.Equal(1, finished!.Value.l1.ToInt64() & 1); // 成功
        Assert.True(done.Wait(2000));

        lock (moved) Assert.Equal(new[] { (30, 20), (50, 40) }, moved);    // 画面上の位置 → ウィンドウ内の位置
        Assert.Equal(app, dropped!.Value.Window);
        Assert.Equal((50, 40), (dropped.Value.X, dropped.Value.Y));
        Assert.Equal(new[] { "/home/user/clip 1.mp4", "/home/user/画像.png" }, dropped.Value.Files);
    }

    /// <summary>何もないときは、受け取りのスレッドが起きない (以前は終了を確かめるために 1 秒に 10 回起きていた)。終了はすぐ。</summary>
    [Fact]
    public void Idle_receiver_sleeps_until_something_happens_and_stops_at_once()
    {
        var target = XdndDropTarget.TryCreate();
        if (target == null) return;     // X サーバーがない
        try
        {
            Thread.Sleep(300);
            // スレッドの名前 (XDND) で、そのスレッドが眠りから覚めた回数 (自分から手放した回数) を読む
            static long? Wakeups()
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
                {
                    try
                    {
                        if (File.ReadAllText(Path.Combine(dir, "comm")).Trim() != "XDND") continue;
                        var line = File.ReadLines(Path.Combine(dir, "status")).First(l => l.StartsWith("voluntary_ctxt_switches:"));
                        return long.Parse(line.Split(':')[1].Trim());
                    }
                    catch (IOException) { }
                }
                return null;
            }
            var before = Wakeups();
            Assert.NotNull(before);
            Thread.Sleep(1000);
            var after = Wakeups();
            Assert.True(after - before <= 2, $"何もないのに {after - before} 回起きた");
        }
        finally
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            target.Dispose();
            Assert.True(clock.ElapsedMilliseconds < 500, $"終了に {clock.ElapsedMilliseconds} ms かかった");
        }
    }

    [Fact]
    public void Drags_without_files_are_refused()
    {
        using var target = XdndDropTarget.TryCreate();
        if (target == null) return;
        using var src = new FakeSource();
        var app = src.CreateAppWindow(10, 10);
        bool droppedAny = false;
        target.Dropped += (_, _, _, _) => droppedAny = true;
        target.Attach(app);
        Thread.Sleep(50);
        var proxy = src.ProxyOf(app);
        src.Send(proxy, app, src.Atom("XdndEnter"), (IntPtr)(5L << 24), src.Atom("text/plain"));     // 文字だけのドラッグ
        src.Send(proxy, app, src.Atom("XdndPosition"), default, (IntPtr)((20L << 16) | 20), IntPtr.Zero, src.Atom("XdndActionCopy"));
        var status = src.WaitFor(src.Atom("XdndStatus"));
        Assert.NotNull(status);
        Assert.Equal(0, status!.Value.l1.ToInt64() & 1);  // 受け付けない
        src.Send(proxy, app, src.Atom("XdndDrop"), default, IntPtr.Zero);
        var finished = src.WaitFor(src.Atom("XdndFinished"));
        Assert.Equal(0, finished!.Value.l1.ToInt64() & 1);
        Assert.False(droppedAny);
    }
}
