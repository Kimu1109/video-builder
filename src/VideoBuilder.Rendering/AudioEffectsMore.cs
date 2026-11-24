using VideoBuilder.Core;
using VideoBuilder.Media;
using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>
/// 追加した音声のエフェクト: リバーブ・コンプレッサー・リミッター・ノイズゲート・ノイズ除去・ピッチ変更・フィルタ・ラウドネスの正規化。
/// どれもブロック (AudioBlock) ごとに呼ばれ、続きの状態 (残響・包絡線など) を state に持つ。
/// 続いていないブロック (シーク・再生開始の直後) では状態を作り直す。
/// </summary>
public static partial class AudioEffects
{
    private static void RegisterMore()
    {
        Register(new Reverb()); Register(new Compressor()); Register(new Limiter()); Register(new Gate());
        Register(new Denoise()); Register(new Pitch()); Register(new Filter()); Register(new Loudness());
    }

    /// <summary>コンプレッサーが縮める量 (dB。テスト用)。</summary>
    internal static double CompressorReduction(double inDb, double threshold, double ratio, double knee) => Compressor.ReductionDb(inDb, threshold, ratio, knee);
    /// <summary>統合ラウドネス (LUFS。テスト用)。</summary>
    internal static double IntegratedLoudness(Func<long, float[]> read, long total, int rate) => Loudness.Integrated(read, total, rate);

    private static float DbToGain(double db) => (float)Math.Pow(10, db / 20);
    private static double GainToDb(double g) => 20 * Math.Log10(Math.Max(1e-9, g));
    /// <summary>time 秒で約 63% 近づく、1 サンプルあたりの追従の係数。</summary>
    private static float Coef(double seconds, int rate) => (float)(1 - Math.Exp(-1 / Math.Max(1e-6, seconds * rate)));

    // ================= リバーブ =================

    /// <summary>
    /// リバーブ (Freeverb 方式): 8 本の櫛形フィルタ (高音を少しずつ吸収) を並べ、4 本のオールパスフィルタを通す。左右で遅れを少し変えて広がりを出す。
    /// </summary>
    private sealed class Reverb : IAudioEffect
    {
        public string Id => "audio.reverb";
        private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        private const int Spread = 23;

        private sealed class Channel
        {
            public float[][] Combs = null!; public int[] CombPos = null!; public float[] CombStore = null!;
            public float[][] Alls = null!; public int[] AllPos = null!;
        }

        private sealed class State
        {
            public Channel L = null!, R = null!;
            public float[] Pre = null!; public int PrePos;
            public int Rate;
        }

        private static Channel Make(int rate, int spread)
        {
            double k = rate / 44100.0;
            var c = new Channel
            {
                Combs = CombTuning.Select(t => new float[Math.Max(1, (int)((t + spread) * k))]).ToArray(), CombPos = new int[8], CombStore = new float[8],
                Alls = AllpassTuning.Select(t => new float[Math.Max(1, (int)((t + spread) * k))]).ToArray(), AllPos = new int[4],
            };
            return c;
        }

        private static float Run(Channel c, float input, float feedback, float damp)
        {
            float outSum = 0;
            for (int i = 0; i < 8; i++)
            {
                var buf = c.Combs[i];
                ref int p = ref c.CombPos[i];
                float y = buf[p];
                c.CombStore[i] = y * (1 - damp) + c.CombStore[i] * damp;
                buf[p] = input + c.CombStore[i] * feedback;
                if (++p >= buf.Length) p = 0;
                outSum += y;
            }
            for (int i = 0; i < 4; i++)
            {
                var buf = c.Alls[i];
                ref int p = ref c.AllPos[i];
                float bv = buf[p];
                float y = -outSum + bv;
                buf[p] = outSum + bv * 0.5f;
                if (++p >= buf.Length) p = 0;
                outSum = y;
            }
            return outSum;
        }

        public void Process(AudioBlock b, ref object? state)
        {
            int rate = b.SampleRate;
            float room = Math.Clamp(b.Track("room") / 100f, 0, 1), damp = Math.Clamp(b.Track("damping") / 100f, 0, 1) * 0.4f;
            float mix = Math.Clamp(b.Track("mix") / 100f, 0, 1), width = Math.Clamp(b.Track("width") / 100f, 0, 1);
            int pre = Math.Clamp((int)(b.Track("predelay") / 1000 * rate), 0, rate);
            if (mix <= 0) return;
            if (state is not State st || !b.Continuous || st.Rate != rate || st.Pre.Length != (pre + 1) * 2)
                state = st = new State { L = Make(rate, 0), R = Make(rate, Spread), Pre = new float[(pre + 1) * 2], Rate = rate };
            float feedback = 0.7f + 0.28f * room;
            float wet1 = mix * 3 * (width / 2 + 0.5f), wet2 = mix * 3 * ((1 - width) / 2), dry = 1 - mix * 0.5f;
            var s = b.Samples;
            int preLen = st.Pre.Length / 2;
            for (int i = 0; i < b.FrameCount; i++)
            {
                float l = s[i * 2], r = s[i * 2 + 1];
                // 初期反射までの遅れ
                int pp = st.PrePos;
                float dl = st.Pre[pp * 2], dr = st.Pre[pp * 2 + 1];
                st.Pre[pp * 2] = l; st.Pre[pp * 2 + 1] = r;
                if (++st.PrePos >= preLen) st.PrePos = 0;
                if (preLen == 1) { dl = l; dr = r; }
                float input = (dl + dr) * 0.015f;
                float ol = Run(st.L, input, feedback, damp), or = Run(st.R, input, feedback, damp);
                s[i * 2] = l * dry + ol * wet1 + or * wet2;
                s[i * 2 + 1] = r * dry + or * wet1 + ol * wet2;
            }
        }
    }

    // ================= ダイナミクス =================

    /// <summary>
    /// コンプレッサー: 左右の大きいほうの音量がしきい値を超えたぶんを、圧縮比で縮める (ニーの幅でなめらかに)。
    /// 縮める量はアタック・リリースでなめらかに変える。音量の持ち上げで全体を上げる。
    /// </summary>
    private sealed class Compressor : IAudioEffect
    {
        public string Id => "audio.compressor";
        private sealed class State { public double Env; public double Reduction; }

        /// <summary>入力の音量 (dB) に対して、縮める量 (dB、0 以上)。</summary>
        internal static double ReductionDb(double inDb, double threshold, double ratio, double knee)
        {
            double over = inDb - threshold;
            if (knee > 0 && Math.Abs(over) <= knee / 2)
                return (1 - 1 / ratio) * (over + knee / 2) * (over + knee / 2) / (2 * knee);
            return over > 0 ? over * (1 - 1 / ratio) : 0;
        }

        public void Process(AudioBlock b, ref object? state)
        {
            double threshold = b.Track("threshold"), ratio = Math.Max(1, b.Track("ratio")), knee = Math.Max(0, b.Track("knee"));
            float makeup = DbToGain(b.Track("makeup"));
            int rate = b.SampleRate;
            float atk = Coef(b.Track("attack") / 1000, rate), rel = Coef(b.Track("release") / 1000, rate);
            if (state is not State st || !b.Continuous) state = st = new State();
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                double peak = Math.Max(Math.Abs(s[i * 2]), Math.Abs(s[i * 2 + 1]));
                // 包絡線: 上がるときはアタック、下がるときはリリースで追う
                st.Env += (peak - st.Env) * (peak > st.Env ? atk : rel);
                double target = ReductionDb(GainToDb(st.Env), threshold, ratio, knee);
                st.Reduction += (target - st.Reduction) * (target > st.Reduction ? atk : rel);
                float g = DbToGain(-st.Reduction) * makeup;
                s[i * 2] *= g; s[i * 2 + 1] *= g;
            }
        }
    }

    /// <summary>
    /// リミッター: 上限を超えないように音量を下げる。5ms 先を見て、大きな音が来る前に下げ始める (その分、音は 5ms 遅れる)。
    /// 最後に上限で切り詰めるので、上限を超えることはない。
    /// </summary>
    private sealed class Limiter : IAudioEffect
    {
        public string Id => "audio.limiter";
        private sealed class State { public float[] Delay = null!; public int Pos; public float Gain = 1; public float Env; }

        public void Process(AudioBlock b, ref object? state)
        {
            int rate = b.SampleRate, look = Math.Max(1, rate / 200);
            float ceiling = DbToGain(b.Track("ceiling")), input = DbToGain(b.Track("gain"));
            float rel = Coef(b.Track("release") / 1000, rate), atk = Coef(look / (double)rate / 5, rate);
            float envRelease = (float)Math.Exp(-1 / (look * 1.0));
            if (state is not State st || !b.Continuous || st.Delay.Length != look * 2) state = st = new State { Delay = new float[look * 2] };
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                float l = s[i * 2] * input, r = s[i * 2 + 1] * input;
                float peak = Math.Max(Math.Abs(l), Math.Abs(r));
                // 先読み: 今入ってきた音 (5ms 後に出る音) の大きさで、下げる量を決める
                st.Env = peak > st.Env ? peak : st.Env * envRelease + peak * (1 - envRelease);
                float target = st.Env > ceiling ? ceiling / st.Env : 1;
                st.Gain += (target - st.Gain) * (target < st.Gain ? atk : rel);
                int p = st.Pos;
                float dl = st.Delay[p * 2], dr = st.Delay[p * 2 + 1];
                st.Delay[p * 2] = l; st.Delay[p * 2 + 1] = r;
                if (++st.Pos >= look) st.Pos = 0;
                s[i * 2] = Math.Clamp(dl * st.Gain, -ceiling, ceiling);
                s[i * 2 + 1] = Math.Clamp(dr * st.Gain, -ceiling, ceiling);
            }
        }
    }

    /// <summary>ノイズゲート: 音量がしきい値より小さい間 (保持の時間を過ぎたら) 下げる量だけ下げる。話していない間の小さな雑音を消す。</summary>
    private sealed class Gate : IAudioEffect
    {
        public string Id => "audio.gate";
        private sealed class State { public float Env; public float Gain = 1; public int Hold; }

        public void Process(AudioBlock b, ref object? state)
        {
            int rate = b.SampleRate;
            float threshold = DbToGain(b.Track("threshold")), low = DbToGain(-b.Track("reduction"));
            float atk = Coef(b.Track("attack") / 1000, rate), rel = Coef(b.Track("release") / 1000, rate);
            int hold = (int)(b.Track("hold") / 1000 * rate);
            float envDecay = (float)Math.Exp(-1 / (0.01 * rate));
            if (state is not State st || !b.Continuous) state = st = new State();
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                float peak = Math.Max(Math.Abs(s[i * 2]), Math.Abs(s[i * 2 + 1]));
                st.Env = peak > st.Env ? peak : st.Env * envDecay;
                if (st.Env >= threshold) st.Hold = hold;
                else if (st.Hold > 0) st.Hold--;
                float target = st.Env >= threshold || st.Hold > 0 ? 1 : low;
                st.Gain += (target - st.Gain) * (target > st.Gain ? atk : rel);
                s[i * 2] *= st.Gain; s[i * 2 + 1] *= st.Gain;
            }
        }
    }

    // ================= ノイズ除去 =================

    /// <summary>
    /// ノイズ除去 (スペクトル減算): 短い区間ごとに周波数に分け (窓をかけた FFT)、周波数ごとの雑音の大きさを
    /// 「なめらかにした強さの最小値」で推定して、雑音に近い成分ほど小さくする (ウィーナー型の利得)。
    /// 区間を 4 分の 3 ずつ重ねて足し戻す。区間の長さ (約 21ms) だけ音が遅れる。
    /// </summary>
    private sealed class Denoise : IAudioEffect
    {
        public string Id => "audio.denoise";

        private sealed class Ch
        {
            // 入力は直近 n サンプルの輪 (InPos が次に書く所)、出力は重ねて足す n サンプルの輪 (OlaPos が次に出す所)
            public float[] In = null!; public int InPos, InCount;
            public double[] Ola = null!; public int OlaPos;
            public double[] Smooth = null!, Noise = null!, Gain = null!;
            public bool Started;
            // なめらかにした強さの記録 (4 区間ごとに 1 つ、History 個ぶん = 約 1.5 秒)。雑音はこの中の最小値
            public double[] Hist = null!; public int HistPos, HistCount, Frames;
        }

        private sealed class State
        {
            public int N, Hop, Rate;
            public double[] Window = null!;
            public Ch L = null!, R = null!;
            public int SinceFrame;
            public double[] Re = null!, Im = null!;
            public double OlaScale;
        }

        private static Ch MakeCh(int n) => new()
        {
            In = new float[n], Ola = new double[n], Smooth = new double[n / 2 + 1], Noise = new double[n / 2 + 1], Gain = Enumerable.Repeat(1.0, n / 2 + 1).ToArray(),
            Hist = new double[History * (n / 2 + 1)],
        };

        private const int History = 70, HistoryStep = 4;

        public void Process(AudioBlock b, ref object? state)
        {
            int rate = b.SampleRate;
            double amount = Math.Clamp(b.Track("amount") / 100, 0, 1);
            double floor = Math.Pow(10, -Math.Max(0, b.Track("floor")) / 20);
            int n = rate >= 32000 ? 1024 : 512;
            if (state is not State st || !b.Continuous || st.N != n || st.Rate != rate)
            {
                var win = new double[n];
                for (int i = 0; i < n; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                state = st = new State { N = n, Hop = n / 4, Rate = rate, Window = win, L = MakeCh(n), R = MakeCh(n), Re = new double[n], Im = new double[n] };
                // 分析窓と合成窓の両方にハン窓をかけ、4 分の 3 ずつ重ねたときの和で割る
                st.OlaScale = 1 / (win.Sum(w => w * w) / st.Hop);
            }
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                Push(st.L, s[i * 2], st.N);
                Push(st.R, s[i * 2 + 1], st.N);
                if (++st.SinceFrame >= st.Hop)
                {
                    st.SinceFrame = 0;
                    Frame(st, st.L, amount, floor);
                    Frame(st, st.R, amount, floor);
                }
                // 出力は、重ねて足した結果の先頭 (区間の長さだけ遅れる)
                s[i * 2] = (float)Pop(st.L, st.N);
                s[i * 2 + 1] = (float)Pop(st.R, st.N);
            }
        }

        private static void Push(Ch c, float x, int n)
        {
            c.In[c.InPos] = x;
            if (++c.InPos >= n) c.InPos = 0;
            if (c.InCount < n) c.InCount++;
        }

        private static double Pop(Ch c, int n)
        {
            double y = c.Ola[c.OlaPos];
            c.Ola[c.OlaPos] = 0;
            if (++c.OlaPos >= n) c.OlaPos = 0;
            return y;
        }

        private static void Frame(State st, Ch c, double amount, double floor)
        {
            int n = st.N;
            if (c.InCount < n) return;
            var re = st.Re; var im = st.Im;
            // 輪の古い順 (InPos から) に並べて窓をかける
            for (int i = 0; i < n; i++) { re[i] = c.In[(c.InPos + i) % n] * st.Window[i]; im[i] = 0; }
            Fft(re, im, false);
            int bins = n / 2 + 1;
            bool record = c.Frames % HistoryStep == 0;
            // 始めの約 1.5 秒は、記録が足りず雑音を大きく見積もりがちなので、少しずつ効かせる (頭の音を削らないように)
            double trust = Math.Min(1, c.Frames / (double)(History * HistoryStep));
            c.Frames++;
            for (int k = 0; k < bins; k++)
            {
                double p = re[k] * re[k] + im[k] * im[k];
                // 強さをなめらかにし、直近約 1.5 秒の最小値を雑音とする (鳴っている音は、どこかで小さくなるので残る)
                c.Smooth[k] = c.Started ? 0.8 * c.Smooth[k] + 0.2 * p : p;
                if (record) c.Hist[c.HistPos * bins + k] = c.Smooth[k];
                double min = c.Smooth[k];
                for (int h = 0; h < c.HistCount; h++) min = Math.Min(min, c.Hist[h * bins + k]);
                c.Noise[k] = min * trust;
                double snrNoise = c.Noise[k] * (1 + 3 * amount);
                double g = Math.Max(floor, 1 - amount * snrNoise / Math.Max(1e-20, p));
                g = Math.Max(floor, Math.Min(1, g));
                c.Gain[k] = 0.6 * c.Gain[k] + 0.4 * g;   // 利得もなめらかに (ちりちりした音を防ぐ)
                re[k] *= c.Gain[k]; im[k] *= c.Gain[k];
                if (k > 0 && k < n / 2) { re[n - k] = re[k]; im[n - k] = -im[k]; }
            }
            c.Started = true;
            if (record) { c.HistPos = (c.HistPos + 1) % History; c.HistCount = Math.Min(History, c.HistCount + 1); }
            Fft(re, im, true);
            // これから出す n サンプル (区間の長さだけ遅れる) に重ねて足す
            for (int i = 0; i < n; i++) c.Ola[(c.OlaPos + i) % n] += re[i] * st.Window[i] * st.OlaScale;
        }
    }

    /// <summary>複素数の高速フーリエ変換 (基数 2、その場で書き換える)。inverse なら逆変換 (1/n をかける)。</summary>
    internal static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, bb = i + k + len / 2;
                    double xr = re[bb] * cr - im[bb] * ci, xi = re[bb] * ci + im[bb] * cr;
                    re[bb] = re[a] - xr; im[bb] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    // ================= ピッチ・フィルタ =================

    /// <summary>
    /// ピッチ変更 (速さはそのまま): 遅れの量を少しずつ変える 2 つの読み出し口を、三角の窓で交互に重ねる (遅延線方式)。
    /// 窓は 60ms。声の高さを変える (ボイスチェンジャー) くらいの用途向け。
    /// </summary>
    private sealed class Pitch : IAudioEffect
    {
        public string Id => "audio.pitch";
        private sealed class State { public float[] Buf = null!; public int Pos; public double Phase; }

        public void Process(AudioBlock b, ref object? state)
        {
            double semis = b.Track("semitones") + b.Track("cents") / 100;
            float mix = Math.Clamp(b.Track("mix") / 100f, 0, 1);
            if (Math.Abs(semis) < 1e-3 || mix <= 0) return;
            int rate = b.SampleRate, window = Math.Max(64, (int)(0.06 * rate)), size = window * 2 + 4;
            if (state is not State st || !b.Continuous || st.Buf.Length != size * 2) state = st = new State { Buf = new float[size * 2] };
            double ratio = Math.Pow(2, semis / 12);
            double step = (1 - ratio) / window;   // 1 サンプルで変わる遅れ (窓の長さを 1 として)
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                float l = s[i * 2], r = s[i * 2 + 1];
                st.Buf[st.Pos * 2] = l; st.Buf[st.Pos * 2 + 1] = r;
                st.Phase = (st.Phase + step) % 1;
                if (st.Phase < 0) st.Phase += 1;
                double p1 = st.Phase, p2 = (st.Phase + 0.5) % 1;
                // 三角の窓: 遅れが 0 や窓の端に近い読み出し口ほど小さく
                double w1 = 1 - Math.Abs(p1 * 2 - 1), w2 = 1 - Math.Abs(p2 * 2 - 1);
                var (al, ar) = Read(st, size, p1 * window + 1);
                var (bl, br) = Read(st, size, p2 * window + 1);
                float ol = (float)(al * w1 + bl * w2), or = (float)(ar * w1 + br * w2);
                s[i * 2] = l + (ol - l) * mix;
                s[i * 2 + 1] = r + (or - r) * mix;
                if (++st.Pos >= size) st.Pos = 0;
            }
        }

        private static (double L, double R) Read(State st, int size, double delay)
        {
            double pos = st.Pos - delay;
            while (pos < 0) pos += size;
            int i0 = (int)pos, i1 = (i0 + 1) % size;
            double t = pos - i0;
            return (st.Buf[i0 * 2] + (st.Buf[i1 * 2] - st.Buf[i0 * 2]) * t, st.Buf[i0 * 2 + 1] + (st.Buf[i1 * 2 + 1] - st.Buf[i0 * 2 + 1]) * t);
        }
    }

    /// <summary>双 2 次フィルタ 1 つ (左右別の状態)。</summary>
    private struct Biquad2
    {
        public double B0, B1, B2, A1, A2;
        private double x1l, x2l, y1l, y2l, x1r, x2r, y1r, y2r;

        /// <summary>RBJ Audio EQ Cookbook の係数。kind: 0 ローパス・1 ハイパス・2 バンドパス・3 ノッチ・4 高音シェルフ (gainDb)。</summary>
        public void Set(int kind, double freq, double q, int rate, double gainDb = 0)
        {
            double w = 2 * Math.PI * Math.Clamp(freq, 1, rate * 0.49) / rate, cos = Math.Cos(w), sin = Math.Sin(w), alpha = sin / (2 * Math.Max(0.05, q));
            double b0, b1, b2, a0, a1, a2;
            switch (kind)
            {
                case 0: b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case 1: b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case 2: b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case 3: b0 = 1; b1 = -2 * cos; b2 = 1; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                default:
                {
                    double a = Math.Pow(10, gainDb / 40), sq = 2 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) + (a - 1) * cos + sq); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sq);
                    a0 = (a + 1) - (a - 1) * cos + sq; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sq;
                    break;
                }
            }
            B0 = b0 / a0; B1 = b1 / a0; B2 = b2 / a0; A1 = a1 / a0; A2 = a2 / a0;
        }

        public float L(float x) { double y = B0 * x + B1 * x1l + B2 * x2l - A1 * y1l - A2 * y2l; x2l = x1l; x1l = x; y2l = y1l; y1l = y; return (float)y; }
        public float R(float x) { double y = B0 * x + B1 * x1r + B2 * x2r - A1 * y1r - A2 * y2r; x2r = x1r; x1r = x; y2r = y1r; y1r = y; return (float)y; }
    }

    /// <summary>フィルタ: ローパス・ハイパス・バンドパス・ノッチ。急な傾きなら同じフィルタを 2 段重ねる (24 dB/oct)。</summary>
    private sealed class Filter : IAudioEffect
    {
        public string Id => "audio.filter";
        private sealed class State { public Biquad2 A, B; }

        public void Process(AudioBlock b, ref object? state)
        {
            int kind = Math.Clamp(b.Effect.GetInt("type"), 0, 3);
            double freq = b.Track("frequency"), q = b.Track("q");
            bool steep = b.Check("steep");
            if (state is not State st || !b.Continuous) state = st = new State();
            // 係数は値が変わっても状態を残して計算し直す (音が途切れないように)
            st.A.Set(kind, freq, q, b.SampleRate);
            st.B.Set(kind, freq, q, b.SampleRate);
            var s = b.Samples;
            for (int i = 0; i < b.FrameCount; i++)
            {
                float l = st.A.L(s[i * 2]), r = st.A.R(s[i * 2 + 1]);
                if (steep) { l = st.B.L(l); r = st.B.R(r); }
                s[i * 2] = l; s[i * 2 + 1] = r;
            }
        }
    }

    // ================= ラウドネス =================

    private static readonly Dictionary<string, double> LoudnessCache = new();

    /// <summary>
    /// ラウドネスの正規化: オブジェクトで流れる範囲の素材を最初に 1 回だけ読んで、統合ラウドネス (ITU-R BS.1770 の考え方:
    /// K 特性の重み・400ms の区間・絶対ゲート −70 LUFS と相対ゲート −10 LU) を測り、目標との差だけ音量を変える。
    /// 測った値は、ファイル・開始位置・長さ・速度ごとに覚えておく。測る前のエフェクトの影響は含めない (素材そのものを測る)。
    /// </summary>
    private sealed class Loudness : IAudioEffect
    {
        public string Id => "audio.loudness";

        public void Process(AudioBlock b, ref object? state)
        {
            var obj = b.Object;
            string file = obj.Main.GetString("file");
            if (string.IsNullOrEmpty(file)) return;
            var bounds = obj.Bounds;
            int rate = b.SampleRate;
            double position = obj.Main.Eval("position", bounds, obj.Start) + b.Project.FrameToSeconds(obj.SourceOffsetFrames);
            double speed = Math.Max(0.01, obj.Main.Eval("speed", bounds, obj.Start) / 100);
            double seconds = b.ObjectLength / (double)rate * speed;
            string key = $"{file}|{position:F3}|{seconds:F3}|{rate}";
            double measured;
            lock (LoudnessCache)
            {
                if (!LoudnessCache.TryGetValue(key, out measured))
                {
                    measured = Measure(file, rate, position, seconds);
                    LoudnessCache[key] = measured;
                }
            }
            if (double.IsNegativeInfinity(measured) || double.IsNaN(measured)) return;
            double gainDb = Math.Min(b.Track("target") - measured, b.Track("maxGain"));
            float g = DbToGain(gainDb);
            var s = b.Samples;
            for (int i = 0; i < s.Length; i++) s[i] *= g;
        }

        /// <summary>file の position 秒から seconds 秒の統合ラウドネス (LUFS)。無音・読めなければ −∞。</summary>
        internal static double Measure(string file, int rate, double position, double seconds)
        {
            AudioReader reader;
            try { reader = new AudioReader(file, rate); }
            catch (Exception) { return double.NegativeInfinity; }
            using (reader)
            {
                if (seconds <= 0) seconds = Math.Max(0, reader.Duration - position);
                return Integrated(start => { var buf = new float[rate / 10 * 2]; reader.Read((long)(position * rate) + start, buf); return buf; },
                    (long)(seconds * rate), rate);
            }
        }

        /// <summary>read(開始サンプル) が 100ms ずつのステレオのサンプルを返すとき、total サンプルの統合ラウドネス (LUFS)。</summary>
        internal static double Integrated(Func<long, float[]> read, long total, int rate)
        {
            // K 特性: 高音シェルフ (+4 dB、1.68 kHz) と低音カット (38 Hz)
            var shelf = new Biquad2(); shelf.Set(4, 1681.97, 0.7072, rate, 4.0);
            var hp = new Biquad2(); hp.Set(1, 38.14, 0.5003, rate);
            int step = rate / 10;   // 100ms
            var energies = new List<double>();   // 100ms ごとの平均の強さ
            for (long pos = 0; pos < total; pos += step)
            {
                var buf = read(pos);
                int n = (int)Math.Min(step, total - pos);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float l = hp.L(shelf.L(buf[i * 2])), r = hp.R(shelf.R(buf[i * 2 + 1]));
                    sum += l * l + r * r;
                }
                energies.Add(sum / Math.Max(1, n));
            }
            // 400ms の区間 (100ms ずつずらす) の強さ
            var blocks = new List<double>();
            for (int i = 0; i + 4 <= energies.Count; i++) blocks.Add((energies[i] + energies[i + 1] + energies[i + 2] + energies[i + 3]) / 4);
            if (blocks.Count == 0 && energies.Count > 0) blocks.Add(energies.Average());
            static double Lufs(double e) => -0.691 + 10 * Math.Log10(Math.Max(1e-20, e));
            var abs = blocks.Where(e => Lufs(e) > -70).ToList();
            if (abs.Count == 0) return double.NegativeInfinity;
            double rel = Lufs(abs.Average()) - 10;
            var gated = abs.Where(e => Lufs(e) > rel).ToList();
            return Lufs((gated.Count > 0 ? gated : abs).Average());
        }
    }
}
