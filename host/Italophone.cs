// Italophone: mouse, teclado e som do notebook no celular Android, pelo cabo USB.
//
// - Mouse e teclado: o celular fica "encostado" em um lado de uma das telas (Organizar telas).
//   Empurrando o mouse para fora dessa borda, ele some do Windows e aparece a setinha do proprio
//   Android no celular (um mouse virtual criado pelo servidor italophone-servidor.jar via /dev/uhid).
//   O teclado vai junto. Levando a setinha de volta a borda do celular, o mouse volta ao Windows.
// - Som: o som do celular toca no notebook (scrcpy, so audio).
// - Tela: opcional, mostra a tela do celular numa janela (scrcpy).
// - Area de transferencia: copiar texto em qualquer lado poe no outro. O Android bloqueia a
//   leitura da area de transferencia em segundo plano a quem nao for o teclado padrao; por isso o
//   celular vira o proprio teclado (vazio) enquanto conectado e volta ao de antes ao desconectar.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Italophone")]
[assembly: System.Reflection.AssemblyProduct("Italophone")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyCompany("Ítalo Sêne")]
[assembly: System.Reflection.AssemblyCopyright("MIT")]

static class Nat
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLL { public POINT pt; public int mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLL { public int vk, scan, flags, time; public IntPtr extra; }
    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, int tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(int tid, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
}

// ===================================================================== configuracao
class Config
{
    public string Celular = "";              // numero de serie do celular no adb ("" = o primeiro que aparecer)
    public string Lado = "direita";          // direita, esquerda, cima, baixo (lado da tela em que o celular fica)
    public Rectangle Monitor = Rectangle.Empty;
    public int Inicio = int.MinValue;        // posicao do celular ao longo da borda (pixels do Windows)
    public double Escala = 0;                // tamanho: pixels do celular por pixel do Windows (0 = automatico)
    public double Velocidade = 0;            // pixels do celular andados por pixel do Windows (0 = padrao)
    public const double VelocidadePadrao = 1.5;
    public bool Mouse = true, Som = true, Midia = true, Clip = true;   // Midia: teclas de volume e musica vao para o celular; Clip: area de transferencia
    public int CelW = 1080, CelH = 2400;     // ultimo tamanho conhecido da tela do celular

    // macros de teclado -> celular, so com o mouse dentro dele. Configuraveis pelo menu
    // ("Configurar atalhos...") ou, direto no config.txt, uma linha por atalho:
    //   macro=<combo>=<acao>
    // combo: ctrl/alt/shift/win (0 ou mais) + uma tecla, separados por "+" (ex.: "ctrl+alt+c",
    // "calculadora", "proxima"). Teclas aceitas: letras, numeros, f1..f24, as teclas de volume e
    // midia (mudo/volmenos/volmais/proxima/anterior/parar/tocapausa), as teclas de atalho do
    // teclado que existirem (correio/midia/app1/calculadora),
    // esc/tab/espaco/enter/home/fim/delete/insert/pageup/pagedown/cima/baixo/esquerda/direita, ou
    // qualquer outra tecla pelo codigo Windows ("vk186" etc., o que o menu usa para o que nao tem
    // nome definido aqui).
    // acao: nome de pacote Android (abre o app) ou voltar/home/recentes/notificacoes.
    // Ex.: macro=ctrl+alt+c=com.google.android.calculator
    public class ItemMacro { public string Combo, Acao; }
    public readonly List<ItemMacro> Lista = new List<ItemMacro>();                      // atalhos, na ordem do arquivo
    public readonly Dictionary<long, string> Macros = new Dictionary<long, string>();   // combo (chave) -> acao, p/ o gancho

    static readonly Dictionary<string, int> NomeParaVk = ConstruirNomes();
    static readonly Dictionary<int, string> VkParaNome = ConstruirNomesInverso();
    static Dictionary<string, int> ConstruirNomes()
    {
        var d = new Dictionary<string, int>();
        for (char c = 'a'; c <= 'z'; c++) d[c.ToString()] = c - 'a' + 0x41;
        for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c - '0' + 0x30;
        for (int i = 1; i <= 24; i++) d["f" + i] = 0x70 + i - 1;
        d["mudo"] = 0xAD; d["volmenos"] = 0xAE; d["volmais"] = 0xAF;
        d["proxima"] = 0xB0; d["anterior"] = 0xB1; d["parar"] = 0xB2; d["tocapausa"] = 0xB3;
        d["correio"] = 0xB4; d["midia"] = 0xB5; d["app1"] = 0xB6; d["calculadora"] = 0xB7;
        d["esc"] = 0x1B; d["tab"] = 0x09; d["espaco"] = 0x20; d["enter"] = 0x0D;
        d["home"] = 0x24; d["fim"] = 0x23; d["delete"] = 0x2E; d["insert"] = 0x2D;
        d["pageup"] = 0x21; d["pagedown"] = 0x22;
        d["cima"] = 0x26; d["baixo"] = 0x28; d["esquerda"] = 0x25; d["direita"] = 0x27;
        return d;
    }
    static Dictionary<int, string> ConstruirNomesInverso()
    {
        var d = new Dictionary<int, string>();
        foreach (var kv in NomeParaVk) if (!d.ContainsKey(kv.Value)) d[kv.Value] = kv.Key;
        return d;
    }

    // uma combinacao vira um numero so: 4 bits de modificador + o vk. Usado tanto para guardar o
    // que veio do config.txt quanto para procurar, a cada tecla, o que esta pressionado agora.
    public static long ChaveCombo(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        return (ctrl ? 1L : 0) | (alt ? 2L : 0) | (shift ? 4L : 0) | (win ? 8L : 0) | ((long)vk << 4);
    }

    // texto de um combo a partir do que esta pressionado agora (usado pelo "Configurar atalhos...")
    public static string TextoCombo(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        var partes = new List<string>();
        if (ctrl) partes.Add("ctrl");
        if (alt) partes.Add("alt");
        if (shift) partes.Add("shift");
        if (win) partes.Add("win");
        string nome;
        partes.Add(VkParaNome.TryGetValue(vk, out nome) ? nome : ("vk" + vk));
        return string.Join("+", partes);
    }

    public static bool AnalisarCombo(string combo, out long chave)
    {
        chave = 0;
        bool ctrl = false, alt = false, shift = false, win = false; int vk = -1, v;
        foreach (var bruto in combo.Split('+'))
        {
            string tok = bruto.Trim().ToLowerInvariant();
            if (tok == "ctrl") ctrl = true;
            else if (tok == "alt") alt = true;
            else if (tok == "shift") shift = true;
            else if (tok == "win") win = true;
            else if (NomeParaVk.TryGetValue(tok, out v)) vk = v;
            else if (tok.StartsWith("vk") && int.TryParse(tok.Substring(2), out v)) vk = v;
            else return false;
        }
        if (vk < 0) return false;
        chave = ChaveCombo(ctrl, alt, shift, win, vk);
        return true;
    }

    // refaz o dicionario usado pelo gancho a partir da lista (depois de adicionar/editar/remover um atalho)
    public void ReconstruirMacros()
    {
        Macros.Clear();
        foreach (var m in Lista)
        {
            long chave;
            if (AnalisarCombo(m.Combo, out chave)) Macros[chave] = m.Acao;
        }
    }

    readonly string arq;
    public Config(string arq) { this.arq = arq; Ler(); }

    void Ler()
    {
        if (!File.Exists(arq)) return;
        foreach (var l in File.ReadAllLines(arq))
        {
            var p = l.Split(new[] { '=' }, 2);
            if (p.Length != 2) continue;
            string k = p[0].Trim(), v = p[1].Trim();
            try
            {
                switch (k)
                {
                    case "celular": if (v != "") Celular = v; break;
                    case "lado": Lado = v; break;
                    case "monitor":
                        var n = v.Split(',');
                        Monitor = new Rectangle(int.Parse(n[0]), int.Parse(n[1]), int.Parse(n[2]), int.Parse(n[3]));
                        break;
                    case "inicio": Inicio = int.Parse(v); break;
                    case "escala": Escala = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "velocidade": Velocidade = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "mouse": Mouse = v != "0"; break;
                    case "som": Som = v != "0"; break;
                    case "midia": Midia = v != "0"; break;
                    case "clip": Clip = v != "0"; break;
                    case "macro":
                        var mp = v.Split(new[] { '=' }, 2);
                        if (mp.Length == 2) Lista.Add(new ItemMacro { Combo = mp[0].Trim(), Acao = mp[1].Trim() });
                        break;
                    case "tela_celular":
                        var t = v.Split('x');
                        CelW = int.Parse(t[0]); CelH = int.Parse(t[1]);
                        break;
                }
            }
            catch { }
        }
        ReconstruirMacros();
    }

    public void Gravar()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Italophone (tambem se muda pelo menu do icone)");
        sb.AppendLine("celular=" + Celular);
        sb.AppendLine("lado=" + Lado);
        if (!Monitor.IsEmpty) sb.AppendLine(string.Format("monitor={0},{1},{2},{3}", Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height));
        if (Inicio != int.MinValue) sb.AppendLine("inicio=" + Inicio);
        if (Escala > 0) sb.AppendLine("escala=" + Escala.ToString("0.000", CultureInfo.InvariantCulture));
        if (Velocidade > 0) sb.AppendLine("velocidade=" + Velocidade.ToString("0.00", CultureInfo.InvariantCulture));
        sb.AppendLine("mouse=" + (Mouse ? 1 : 0));
        sb.AppendLine("som=" + (Som ? 1 : 0));
        sb.AppendLine("midia=" + (Midia ? 1 : 0));
        sb.AppendLine("clip=" + (Clip ? 1 : 0));
        sb.AppendLine("tela_celular=" + CelW + "x" + CelH);
        foreach (var m in Lista) sb.AppendLine("macro=" + m.Combo + "=" + m.Acao);
        try { File.WriteAllText(arq, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }
}

// Onde o celular esta encostado, em pixels do Windows
class Geometria
{
    public Rectangle Monitor;     // tela do Windows em que o celular encosta
    public string Lado;
    public Rectangle Cel;         // retangulo do celular (fora da tela)
    public double K;              // tamanho: pixels do celular por pixel do Windows (onde entra e sai)
    public double V;              // velocidade: pixels do celular andados por pixel do Windows
    public int SegIni, SegFim;    // trecho da borda em que da para passar
    public int CelW, CelH;

    public bool Vertical { get { return Lado == "direita" || Lado == "esquerda"; } }
    // borda do celular virada para o notebook: 0 esquerda, 1 direita, 2 cima, 3 baixo
    public int Porta { get { switch (Lado) { case "direita": return 0; case "esquerda": return 1; case "cima": return 3; default: return 2; } } }
    public int CompCel { get { return Vertical ? CelH : CelW; } }   // comprimento da borda do celular (px do celular)
    public int CelIniAoLongo { get { return Vertical ? Cel.Y : Cel.X; } }

    public static Rectangle RetCelular(Rectangle m, string lado, int inicio, int w, int h)
    {
        switch (lado)
        {
            case "esquerda": return new Rectangle(m.Left - w, inicio, w, h);
            case "cima": return new Rectangle(inicio, m.Top - h, w, h);
            case "baixo": return new Rectangle(inicio, m.Bottom, w, h);
            default: return new Rectangle(m.Right, inicio, w, h);
        }
    }

    public static double EscalaPadrao(Rectangle m, int celW, int celH)
    {
        // o celular em pe ocupa uns 3/4 da altura da tela
        return Math.Max(1.0, celH / (0.75 * m.Height));
    }

    public static Geometria De(Config c, int celW, int celH)
    {
        var telas = Screen.AllScreens;
        Rectangle m = Rectangle.Empty;
        foreach (var s in telas) if (s.Bounds == c.Monitor) m = s.Bounds;
        if (m.IsEmpty)
        {
            // sem configuracao (ou a tela sumiu): a borda direita da tela mais a direita
            foreach (var s in telas) if (m.IsEmpty || s.Bounds.Right > m.Right) m = s.Bounds;
        }
        var g = new Geometria { Monitor = m, Lado = c.Lado, CelW = celW, CelH = celH };
        g.K = c.Escala > 0 ? c.Escala : EscalaPadrao(m, celW, celH);
        g.V = c.Velocidade > 0 ? c.Velocidade : Config.VelocidadePadrao;
        int w = (int)Math.Round(celW / g.K), h = (int)Math.Round(celH / g.K);
        int inicio = c.Inicio;
        if (inicio == int.MinValue || c.Monitor != m)
            inicio = g.Vertical ? m.Top + (m.Height - h) / 2 : m.Left + (m.Width - w) / 2;
        g.Cel = RetCelular(m, g.Lado, inicio, w, h);
        if (g.Vertical) { g.SegIni = Math.Max(m.Top, g.Cel.Top); g.SegFim = Math.Min(m.Bottom, g.Cel.Bottom); }
        else { g.SegIni = Math.Max(m.Left, g.Cel.Left); g.SegFim = Math.Min(m.Right, g.Cel.Right); }
        return g;
    }
}

// ===================================================================== teclado: Windows -> HID
static class Teclas
{
    static readonly Dictionary<int, byte> normal = new Dictionary<int, byte>();
    static readonly Dictionary<int, byte> estendida = new Dictionary<int, byte>();

    static Teclas()
    {
        int[] n = {
            0x01,0x29, 0x02,0x1E, 0x03,0x1F, 0x04,0x20, 0x05,0x21, 0x06,0x22, 0x07,0x23, 0x08,0x24, 0x09,0x25, 0x0A,0x26, 0x0B,0x27,
            0x0C,0x2D, 0x0D,0x2E, 0x0E,0x2A, 0x0F,0x2B,
            0x10,0x14, 0x11,0x1A, 0x12,0x08, 0x13,0x15, 0x14,0x17, 0x15,0x1C, 0x16,0x18, 0x17,0x0C, 0x18,0x12, 0x19,0x13,
            0x1A,0x2F, 0x1B,0x30, 0x1C,0x28, 0x1D,0xE0,
            0x1E,0x04, 0x1F,0x16, 0x20,0x07, 0x21,0x09, 0x22,0x0A, 0x23,0x0B, 0x24,0x0D, 0x25,0x0E, 0x26,0x0F,
            0x27,0x33, 0x28,0x34, 0x29,0x35, 0x2A,0xE1, 0x2B,0x32,
            0x2C,0x1D, 0x2D,0x1B, 0x2E,0x06, 0x2F,0x19, 0x30,0x05, 0x31,0x11, 0x32,0x10,
            0x33,0x36, 0x34,0x37, 0x35,0x38, 0x36,0xE5, 0x37,0x55, 0x38,0xE2, 0x39,0x2C, 0x3A,0x39,
            0x3B,0x3A, 0x3C,0x3B, 0x3D,0x3C, 0x3E,0x3D, 0x3F,0x3E, 0x40,0x3F, 0x41,0x40, 0x42,0x41, 0x43,0x42, 0x44,0x43,
            0x46,0x47, 0x47,0x5F, 0x48,0x60, 0x49,0x61, 0x4A,0x56, 0x4B,0x5C, 0x4C,0x5D, 0x4D,0x5E, 0x4E,0x57,
            0x4F,0x59, 0x50,0x5A, 0x51,0x5B, 0x52,0x62, 0x53,0x63, 0x56,0x64, 0x57,0x44, 0x58,0x45,
            0x73,0x87, 0x7E,0x85 };
        for (int i = 0; i < n.Length; i += 2) normal[n[i]] = (byte)n[i + 1];
        int[] e = {
            0x1C,0x58, 0x1D,0xE4, 0x35,0x54, 0x37,0x46, 0x38,0xE6, 0x47,0x4A, 0x48,0x52, 0x49,0x4B, 0x4B,0x50,
            0x4D,0x4F, 0x4F,0x4D, 0x50,0x51, 0x51,0x4E, 0x52,0x49, 0x53,0x4C, 0x5B,0xE3, 0x5C,0xE7, 0x5D,0x65 };
        for (int i = 0; i < e.Length; i += 2) estendida[e[i]] = (byte)e[i + 1];
    }

    public static byte Hid(int vk, int scan, bool ext)
    {
        if (vk == 0x90) return 0x53;   // Num Lock
        if (vk == 0x13) return 0x48;   // Pause
        if (vk == 0x2C) return 0x46;   // Print Screen
        byte u;
        if (ext ? estendida.TryGetValue(scan, out u) : normal.TryGetValue(scan, out u)) return u;
        return 0;
    }
}

// ===================================================================== conexao com o servidor do celular
class Servidor
{
    readonly App app;
    public volatile bool Pronto;
    // cada conexao tem a propria fila, canal e linhas: numa reconexao, as linhas da conexao velha
    // terminam sozinhas e nao mexem na nova
    class Conexao
    {
        public TcpClient Cli; public NetworkStream St;
        public readonly BlockingCollection<object> Fila = new BlockingCollection<object>();
        public volatile bool Viva = true;
    }
    volatile Conexao con;
    [ThreadStatic] static Conexao atual;   // a conexao da linha de envio que esta rodando
    void Por(object o) { var k = con; if (k != null) { try { k.Fila.Add(o); } catch { } } }
    readonly Stopwatch relogio = Stopwatch.StartNew();
    long ultimoMov = -100000;

    // comandos
    class Mov { public int Dx, Dy; }
    class Bot { public byte B; public sbyte Roda, RodaH; }
    class Tec { public byte[] R; }
    class Bruto { public byte[] B; }
    class Entrada { public int Porta, Alvo, Atual; public bool Conhecido; }

    public Servidor(App app) { this.app = app; }

    public bool Conectar(int porta)
    {
        try
        {
            var c = new TcpClient { NoDelay = true };
            c.Connect("127.0.0.1", porta);
            c.ReceiveTimeout = 5000;
            var s = c.GetStream();
            int r = s.ReadByte();
            // 'R' servidor antigo; 'S' novo, seguido de onde a setinha nasce ao voltar (C meio, K no lugar, U nao se sabe)
            if (r == 'S') Nasce = (char)s.ReadByte();
            else if (r == 'R') Nasce = 'K';
            else { c.Close(); return false; }
            c.ReceiveTimeout = 0;
            Fechar();
            var k = new Conexao { Cli = c, St = s };
            con = k;
            Pronto = true;
            new Thread(() => { atual = k; Enviar(); }) { IsBackground = true, Name = "envio" }.Start();
            // o celular pode mandar de novo onde a setinha nasce (medida feita so na primeira entrada)
            var leitor = new Thread(() =>
            {
                try { int b; while ((b = s.ReadByte()) >= 0) if (b == 'C' || b == 'K' || b == 'U') Nasce = (char)b; } catch { }
                k.Viva = false;
                try { k.Fila.Add(null); } catch { }
                if (con == k) Pronto = false;
            }) { IsBackground = true };
            leitor.Start();
            return true;
        }
        catch { return false; }
    }

    public void Fechar()
    {
        var k = con;
        con = null;
        Pronto = false;
        if (k == null) return;
        k.Viva = false;
        try { k.Cli.Close(); } catch { }
        try { k.Fila.Add(null); } catch { }
    }

    public volatile char Nasce = 'K';
    public void Mover(int dx, int dy) { Por(new Mov { Dx = dx, Dy = dy }); }
    public void Botoes(byte b, int roda, int rodaH) { Por(new Bot { B = b, Roda = (sbyte)Math.Max(-127, Math.Min(127, roda)), RodaH = (sbyte)Math.Max(-127, Math.Min(127, rodaH)) }); }
    public void Teclado(byte[] r) { Por(new Tec { R = (byte[])r.Clone() }); }
    public void Acordar() { Por(new Bruto { B = new[] { (byte)'W' } }); }
    public void Midia(int uso) { Por(new Bruto { B = new[] { (byte)'C', (byte)uso, (byte)(uso >> 8) } }); }
    public void ApagarSetinha() { Por(new Bruto { B = new[] { (byte)'P' } }); }
    public void Entrar(int porta, int alvo, int atual, bool conhecido) { Por(new Entrada { Porta = porta, Alvo = alvo, Atual = atual, Conhecido = conhecido }); }

    void Escrever(byte[] b)
    {
        var k = atual;
        try { k.St.Write(b, 0, b.Length); }
        catch { k.Viva = false; if (con == k) Pronto = false; }
    }

    byte botoes;
    void EscreverMov(int dx, int dy)
    {
        while (dx != 0 || dy != 0)
        {
            int px = Math.Max(-32767, Math.Min(32767, dx)), py = Math.Max(-32767, Math.Min(32767, dy));
            Escrever(new byte[] { (byte)'M', botoes, (byte)px, (byte)(px >> 8), (byte)py, (byte)(py >> 8), 0, 0 });
            dx -= px; dy -= py;
        }
        ultimoMov = relogio.ElapsedMilliseconds;
    }

    // O Android acelera a setinha conforme a velocidade dos ultimos movimentos. Um movimento isolado
    // (mais de 0,5 s depois do anterior) anda exatamente o que se pede: e assim que a setinha e posta
    // no ponto certo ao entrar no celular.
    void EsperarParado()
    {
        long falta = ultimoMov + 520 - relogio.ElapsedMilliseconds;
        if (falta > 0) Thread.Sleep((int)falta);
    }

    void Enviar()
    {
        int accX = 0, accY = 0;
        long segurarAte = 0;
        var fila = atual.Fila;
        while (atual.Viva)
        {
            object o;
            int espera = segurarAte > 0 ? (int)Math.Max(1, segurarAte - relogio.ElapsedMilliseconds) : -1;
            if (!fila.TryTake(out o, espera))
            {
                // acabou o tempo de segurar: solta o movimento acumulado de uma vez
                segurarAte = 0;
                if (accX != 0 || accY != 0) { EscreverMov(accX, accY); accX = accY = 0; }
                continue;
            }
            if (o == null) break;
            var m = o as Mov;
            if (m != null)
            {
                accX += m.Dx; accY += m.Dy;
                if (segurarAte > 0 && relogio.ElapsedMilliseconds < segurarAte) continue;
                segurarAte = 0;
                // junta o que ja estiver na fila
                object prox;
                while (fila.Count > 0 && fila.TryTake(out prox))
                {
                    var m2 = prox as Mov;
                    if (m2 == null) { Processar(prox, ref accX, ref accY, ref segurarAte); break; }
                    accX += m2.Dx; accY += m2.Dy;
                }
                if (accX != 0 || accY != 0) { EscreverMov(accX, accY); accX = accY = 0; }
                continue;
            }
            Processar(o, ref accX, ref accY, ref segurarAte);
        }
    }

    void Processar(object o, ref int accX, ref int accY, ref long segurarAte)
    {
        var b = o as Bot;
        if (b != null)
        {
            if (accX != 0 || accY != 0) { EscreverMov(accX, accY); accX = accY = 0; }
            botoes = b.B;
            Escrever(new byte[] { (byte)'M', botoes, 0, 0, 0, 0, (byte)b.Roda, (byte)b.RodaH });
            return;
        }
        var t = o as Tec;
        if (t != null) { var r = new byte[9]; r[0] = (byte)'K'; Array.Copy(t.R, 0, r, 1, 8); Escrever(r); return; }
        var br = o as Bruto;
        if (br != null) { Escrever(br.B); return; }
        var e = o as Entrada;
        if (e != null)
        {
            accX = accY = 0;
            Escrever(new[] { (byte)'W' });
            // "batida": empurra a setinha contra a borda do lado do notebook (e, se a posicao for
            // desconhecida, tambem contra o comeco da borda), depois anda o que falta
            int sx = 0, sy = 0;
            switch (e.Porta) { case 0: sx = -32767; break; case 1: sx = 32767; break; case 2: sy = -32767; break; case 3: sy = 32767; break; }
            bool vertical = e.Porta <= 1;
            int delta;
            // 'C': o mouse do celular e novo a cada entrada (sem historico de velocidade), entao o
            // primeiro movimento ja anda exato e nao precisa esperar
            bool novo = Nasce == 'C';
            if (!novo) EsperarParado();
            if (!e.Conhecido)
            {
                if (vertical) EscreverMov(sx, -32767); else EscreverMov(-32767, sy);
                EsperarParado();
                delta = e.Alvo;
            }
            else delta = e.Alvo - e.Atual;
            if (vertical) EscreverMov(sx, delta); else EscreverMov(delta, sy);
            if (delta == 0) EscreverMov(0, 0);
            // segura o movimento do usuario um pouco para a "batida" nao contar como velocidade
            // (no mouse novo bastam 30 ms, medido num Android 16; antes eram 120 e a setinha "agarrava")
            segurarAte = relogio.ElapsedMilliseconds + (novo ? 30 : 120);
        }
    }
}

// ===================================================================== app do celular (borda)
class AppBorda
{
    TcpClient cli;
    NetworkStream st;
    public volatile bool Pronto;
    public volatile bool NaBorda;
    public volatile int PosBorda;
    public int W, H;
    public event Action<int, int> Tamanho;
    public event Action<string> ClipRecebido;

    public bool Conectar(int porta)
    {
        try
        {
            var c = new TcpClient { NoDelay = true };
            c.Connect("127.0.0.1", porta);
            var velho = cli;
            if (velho != null) { try { velho.Close(); } catch { } }
            cli = c; st = c.GetStream();
            Pronto = true;
            var t = new Thread(Ler) { IsBackground = true, Name = "borda" };
            t.Start(c);
            return true;
        }
        catch { return false; }
    }

    void Ler(object o)
    {
        var c = (TcpClient)o;
        try
        {
            var r = new StreamReader(c.GetStream(), Encoding.ASCII);
            string l;
            while ((l = r.ReadLine()) != null)
            {
                var p = l.Split(' ');
                if (p[0] == "H" && p.Length == 3) { PosBorda = int.Parse(p[2]); NaBorda = p[1] == "1"; }
                else if (p[0] == "S" && p.Length == 3)
                {
                    int w = int.Parse(p[1]), h = int.Parse(p[2]);
                    if (w != W || h != H) { W = w; H = h; var ev = Tamanho; if (ev != null) ev(w, h); }
                }
                else if (p[0] == "C" && p.Length == 2)
                {
                    try
                    {
                        string texto = Encoding.UTF8.GetString(Convert.FromBase64String(p[1]));
                        var ev = ClipRecebido; if (ev != null) ev(texto);
                    }
                    catch { }
                }
            }
        }
        catch { }
        // so a conexao atual decide (uma velha terminando depois nao derruba a nova)
        if (cli == c) { Pronto = false; NaBorda = false; }
    }

    public void Mostrar(int lado)
    {
        if (!lado.Equals(-1)) NaBorda = false;
        try { var b = Encoding.ASCII.GetBytes("B " + lado + "\n"); st.Write(b, 0, b.Length); } catch { Pronto = false; }
    }

    public void Fechar() { Pronto = false; try { if (cli != null) cli.Close(); } catch { } }

    public void EnviarClip(string texto)
    {
        try { var b = Encoding.ASCII.GetBytes("T " + Convert.ToBase64String(Encoding.UTF8.GetBytes(texto)) + "\n"); st.Write(b, 0, b.Length); }
        catch { Pronto = false; }
    }
}

// ===================================================================== janelinha que esconde o cursor
class Capa : Form
{
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x80000 | 0x08000000;   // toolwindow, topmost, layered, noactivate
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    public event Action ClipboardMudou;
    public Capa()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.01;
        Size = new Size(1, 1);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x20) { Nat.SetCursor(IntPtr.Zero); m.Result = (IntPtr)1; return; }   // WM_SETCURSOR: sem cursor
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE: nao ativar
        if (m.Msg == 0x031D) { var ev = ClipboardMudou; if (ev != null) ev(); }   // WM_CLIPBOARDUPDATE
        base.WndProc(ref m);
    }
    public void Cobrir(int x, int y)
    {
        Nat.SetWindowPos(Handle, new IntPtr(-1), x - 1, y - 1, 3, 3, 0x0010 | 0x0040);
        if (!Visible) Show();
        Nat.SetCursorPos(x, y);   // faz o Windows perguntar o cursor de novo (sem cursor)
    }
    public void Descobrir() { if (Visible) Hide(); }
}

// ===================================================================== um scrcpy mantido vivo
class Scrcpy
{
    public string Nome;
    public Func<string> Opcoes;
    public bool Ligado;
    public Process Proc;
    public DateTime Inicio, Espera = DateTime.MinValue;
    public Action AoSair;
    public bool Vivo { get { try { return Proc != null && !Proc.HasExited; } catch { return false; } } }
}

// ===================================================================== programa
class App : ApplicationContext
{
    const int PORTA_SERVIDOR = 27450, PORTA_APP = 27440;
    const string APP_CELULAR = "br.italo.italophone";
    const int VERSAO_APP = 10;   // versionCode do Italophone.apk que vai junto
    const string JAR_CELULAR = "/data/local/tmp/italophone-servidor.jar";

    public readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    readonly string logArq, adb, scrcpyExe;
    public readonly Config Cfg;
    SynchronizationContext ui;

    readonly NotifyIcon ni = new NotifyIcon();
    ToolStripMenuItem miMouse, miSom, miTela, miMidia, miClip;
    Icon icOk, icDentro, icEsperando, icPausado;
    string estado = "";

    readonly Scrcpy som, espelho;
    readonly System.Windows.Forms.Timer ciclo = new System.Windows.Forms.Timer();
    // O Windows desliga calado um gancho que demorou a responder uma vez (tela do UAC, PC travado
    // um instante). O vigia ve o cursor andando sem o gancho ouvir nada e instala os ganchos de novo.
    readonly System.Windows.Forms.Timer vigiaGancho = new System.Windows.Forms.Timer();
    long ultimoGancho = Environment.TickCount;
    Nat.POINT cursorVigia;
    int suspeita;

    // celular
    Process procServidor;
    readonly Servidor serv;
    readonly AppBorda borda = new AppBorda();
    DateTime proxServidor = DateTime.MinValue, proxApp = DateTime.MinValue;
    bool appPreparado, jarEnviado;
    volatile bool presente;
    string clipUltimoEnviado;

    // mouse
    volatile Geometria geo;
    volatile bool dentro;
    volatile bool armado = true;
    Nat.POINT ancora;
    double restoX, restoY;
    double estX, estY;              // estimativa da setinha no celular (px do celular)
    int forcaFora;   // quanto o usuario empurrou para fora com a setinha ja encostada na borda
    bool posConhecida; int posAoLongo;   // posicao exata ao longo da borda, sabida na ultima saida
    byte botoes;
    int rodaAcc, rodaHAcc;
    readonly byte[] relTeclado = new byte[8];
    readonly List<byte> teclasNoCelular = new List<byte>();
    readonly HashSet<int> vkNoCelular = new HashSet<int>();
    readonly HashSet<int> teclasMacro = new HashSet<int>();   // vk's de macro ja disparados (ainda seguradas)
    // estado de ctrl/alt/shift/win, atualizado a mao a partir do proprio gancho (ver GanchoTeclado):
    // o GetAsyncKeyState do codigo generico (0x11/0x12/0x10) nao refletia de forma confiavel o
    // especifico de lado que o teclado dele manda (0xA2/0xA3 etc.) — confirmado por log e teste isolado
    bool modCtrl, modAlt, modShift, modWin;
    Capa capa;
    Thread ganchoThread;
    int ganchoTid;
    Nat.HookProc procMouse, procTeclado;
    IntPtr hMouse, hTeclado;

    public App()
    {
        logArq = Path.Combine(Dir, "italophone.log");
        scrcpyExe = Path.Combine(Dir, @"scrcpy\scrcpy.exe");
        Cfg = new Config(Path.Combine(Dir, "config.txt"));
        NomesApp.Inicializar(Path.Combine(Dir, "nomes_apps.txt"));
        adb = AcharAdb();
        Environment.SetEnvironmentVariable("ADB", adb);
        serv = new Servidor(this);
        Log("aberto (celular " + Cfg.Celular + ")");
        // scrcpy que sobrou de um Italophone fechado a forca (o som tocaria dobrado): so existe um
        // Italophone por vez (mutex), entao todo scrcpy da nossa pasta agora e orfao
        foreach (var pr in Process.GetProcessesByName("scrcpy"))
        {
            try
            {
                if (pr.MainModule.FileName.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) { pr.Kill(); Log("scrcpy orfao fechado (" + pr.Id + ")"); }
            }
            catch { }
        }

        icOk = Bolinha(Color.FromArgb(46, 160, 67));
        icDentro = Bolinha(Color.FromArgb(30, 110, 220));
        icEsperando = Bolinha(Color.FromArgb(140, 140, 140));
        icPausado = Bolinha(Color.FromArgb(200, 80, 60));

        som = new Scrcpy
        {
            Nome = "som", Ligado = Cfg.Som,
            Opcoes = () => "-s " + Cfg.Celular + " --no-video --no-window --no-control --require-audio --audio-buffer=60 --port=27400:27409"
        };
        espelho = new Scrcpy
        {
            Nome = "tela", Ligado = false,
            Opcoes = () => "-s " + Cfg.Celular + " --no-audio --window-title=\"Tela do celular\" --max-size=1600 --port=27420:27429",
            AoSair = () => { espelho.Ligado = false; ui.Post(_ => miTela.Checked = false, null); }   // fechou a janela: so desmarca
        };

        MontarMenu();
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        capa = new Capa();
        var h = capa.Handle;   // cria a janela na thread da interface
        Nat.AddClipboardFormatListener(h);
        capa.ClipboardMudou += () => ClipboardMudouLocal();

        borda.Tamanho += (w, hh) => ui.Post(_ => NovoTamanho(w, hh), null);
        borda.ClipRecebido += texto => ui.Post(_ => ClipDoCelularRecebido(texto), null);
        AtualizarGeometria();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => ui.Post(_ => AtualizarGeometria(), null);

        IniciarGanchos();

        ciclo.Interval = 3000;
        ciclo.Tick += (s, e) => Ciclo();
        vigiaGancho.Interval = 1000;
        vigiaGancho.Tick += (s, e) =>
        {
            try
            {
                Nat.POINT c;
                if (!Nat.GetCursorPos(out c)) return;
                bool andou = c.X != cursorVigia.X || c.Y != cursorVigia.Y;
                cursorVigia = c;
                bool calado = unchecked(Environment.TickCount - (int)Interlocked.Read(ref ultimoGancho)) > 1500;
                // um pulo so (SetCursorPos, deste ou de outro programa) nao passa pelo gancho: exige o
                // cursor andando dois segundos seguidos sem o gancho ouvir nada
                suspeita = andou && !dentro && calado ? suspeita + 1 : 0;
                if (suspeita >= 2)
                {
                    suspeita = 0;
                    Log("o Windows desligou os ganchos do mouse; instalando de novo");
                    ReinstalarGanchos();
                }
            }
            catch (Exception ex) { Log("vigia do gancho: " + ex.Message); }
        };
        vigiaGancho.Start();
        ciclo.Start();
        AtualizarIcone();
        Ciclo();
    }

    // ---------------------------------------------------------------- utilidades
    string AcharAdb()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { string c = Path.Combine(d.Trim(), "adb.exe"); if (d.Trim() != "" && File.Exists(c)) return c; } catch { }
        }
        try
        {
            string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
            foreach (var p in Directory.GetDirectories(winget, "Google.PlatformTools*"))
            {
                var a = Directory.GetFiles(p, "adb.exe", SearchOption.AllDirectories);
                if (a.Length > 0) return a[0];
            }
        }
        catch { }
        return Path.Combine(Dir, @"scrcpy\adb.exe");
    }

    readonly object logTrava = new object();
    public void Log(string t)
    {
        lock (logTrava)
        {
            try
            {
                var fi = new FileInfo(logArq);
                if (fi.Exists && fi.Length > 300 * 1024) fi.Delete();
                File.AppendAllText(logArq, DateTime.Now.ToString("dd/MM HH:mm:ss") + "  " + t + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    // executa a acao de uma macro no celular: um pacote abre o app (sem precisar saber o nome da
    // activity principal); os nomes especiais mandam a tecla/comando equivalente. Roda fora da
    // linha do gancho de teclado, que nao pode travar.
    // nomes de acao fixa aceitos pela macro e o texto pro menu "Configurar atalhos..."
    public static readonly string[] AcoesFixas = { "voltar", "home", "recentes", "notificacoes", "print", "reiniciar", "desligar", "mudo", "wifi", "dados", "bluetooth", "rotacao", "nfc" };
    public static readonly string[] AcoesFixasTexto = { "Voltar", "Tela inicial", "Apps recentes", "Notificações", "Tirar print", "Reiniciar o celular", "Desligar o celular", "Mudo (alternar)", "Wi-Fi (ligar/desligar)", "Dados móveis (ligar/desligar)", "Bluetooth (ligar/desligar)", "Rotação automática (ligar/desligar)", "NFC (ligar/desligar)" };

    void DispararMacro(string acao)
    {
        string cel = Cfg.Celular;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            switch (acao)
            {
                case "voltar": Adb("-s " + cel + " shell input keyevent 4", 8000); break;
                case "home": Adb("-s " + cel + " shell input keyevent 3", 8000); break;
                case "recentes": Adb("-s " + cel + " shell input keyevent 187", 8000); break;
                case "notificacoes": Adb("-s " + cel + " shell cmd statusbar expand-notifications", 8000); break;
                case "print": Adb("-s " + cel + " shell input keyevent 120", 8000); break;           // KEYCODE_SYSRQ: print nativo (fica na galeria)
                case "reiniciar": Adb("-s " + cel + " reboot", 15000); break;
                case "desligar": Adb("-s " + cel + " shell reboot -p", 15000); break;                 // alguns fabricantes bloqueiam sem root; falha calada
                case "mudo": Adb("-s " + cel + " shell input keyevent 164", 8000); break;              // KEYCODE_VOLUME_MUTE: alterna o som que estiver tocando
                case "wifi": Alternar(cel, "wifi_on", "svc wifi"); break;
                case "dados": Alternar(cel, "mobile_data", "svc data"); break;
                case "bluetooth": Alternar(cel, "bluetooth_on", "svc bluetooth"); break;
                case "rotacao": AlternarSystem(cel, "accelerometer_rotation"); break;
                case "nfc": AlternarNfc(cel); break;
                default: Adb("-s " + cel + " shell monkey -p " + acao + " -c android.intent.category.LAUNCHER 1", 8000); break;   // pacote Android: abre o app
            }
        });
    }

    // le "settings get global <chave>" (0/1) e manda o comando svc contrario do estado atual
    void Alternar(string cel, string chaveGlobal, string comandoSvc)
    {
        bool ligado = Adb("-s " + cel + " shell settings get global " + chaveGlobal, 5000).Trim() == "1";
        Adb("-s " + cel + " shell " + comandoSvc + (ligado ? " disable" : " enable"), 8000);
    }

    // rotacao automatica: so existe como "settings put system", sem comando svc equivalente
    void AlternarSystem(string cel, string chaveSystem)
    {
        bool ligado = Adb("-s " + cel + " shell settings get system " + chaveSystem, 5000).Trim() == "1";
        Adb("-s " + cel + " shell settings put system " + chaveSystem + " " + (ligado ? "0" : "1"), 5000);
    }

    // NFC nao tem "settings global" confiavel entre versoes/fabricantes: le o estado direto do servico
    void AlternarNfc(string cel)
    {
        string saida = Adb("-s " + cel + " shell dumpsys nfc", 5000);
        bool ligado = false;
        foreach (var l in saida.Split('\n')) { var t = l.Trim(); if (t.StartsWith("mState=")) { ligado = t.Substring(7).Trim() == "on"; break; } }
        Adb("-s " + cel + " shell svc nfc " + (ligado ? "disable" : "enable"), 8000);
    }

    public string Adb(string args, int ms = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(adb, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var saida = p.StandardOutput.ReadToEndAsync();
                var erro = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return ""; }
                return (saida.Result + erro.Result).Trim();
            }
        }
        catch { return ""; }
    }

    static Icon Bolinha(Color cor)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(cor)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, 11, 6, 10, 20);
                using (var b = new SolidBrush(cor)) g.FillRectangle(b, 13, 9, 6, 13);
            }
            IntPtr hi = bmp.GetHicon();
            var ic = (Icon)Icon.FromHandle(hi).Clone();
            Nat.DestroyIcon(hi);
            return ic;
        }
    }

    void MontarMenu()
    {
        var menu = new ContextMenuStrip();
        miMouse = new ToolStripMenuItem("Mouse e teclado no celular") { Checked = Cfg.Mouse };
        miMouse.Click += (s, e) => { Cfg.Mouse = !Cfg.Mouse; miMouse.Checked = Cfg.Mouse; Cfg.Gravar(); if (!Cfg.Mouse && dentro) Sair_(-1); Ciclo(); };
        miMidia = new ToolStripMenuItem("Teclas de volume e música no celular") { Checked = Cfg.Midia };
        miMidia.Click += (s, e) => { Cfg.Midia = !Cfg.Midia; miMidia.Checked = Cfg.Midia; Cfg.Gravar(); };
        miClip = new ToolStripMenuItem("Área de transferência compartilhada com o celular") { Checked = Cfg.Clip };
        miClip.Click += (s, e) => { Cfg.Clip = !Cfg.Clip; miClip.Checked = Cfg.Clip; Cfg.Gravar(); };
        miTela = new ToolStripMenuItem("Mostrar a tela do celular");
        miTela.Click += (s, e) => AlternarTela();
        miSom = new ToolStripMenuItem("Som do celular no notebook") { Checked = Cfg.Som };
        miSom.Click += (s, e) => { Cfg.Som = !Cfg.Som; som.Ligado = Cfg.Som; miSom.Checked = Cfg.Som; som.Espera = DateTime.MinValue; Cfg.Gravar(); Ciclo(); };
        var miOrg = new ToolStripMenuItem("Organizar telas...");
        miOrg.Click += (s, e) => Organizar();
        var miAtalhos = new ToolStripMenuItem("Configurar atalhos...");
        miAtalhos.Click += (s, e) => AbrirAtalhos();
        var miCel = new ToolStripMenuItem("Celular");
        miCel.DropDownItems.Add("(procurando...)");
        miCel.DropDownOpening += (s, e) =>
        {
            miCel.DropDownItems.Clear();
            var l = ListarCelulares();
            if (l.Count == 0) miCel.DropDownItems.Add(new ToolStripMenuItem("Nenhum celular no cabo") { Enabled = false });
            foreach (var c in l)
            {
                string serie = c[0];
                string nome = (c[2] != "" ? c[2] : serie) + (c[1] == "device" ? "" : c[1] == "unauthorized" ? " (toque em Permitir no celular)" : " (" + c[1] + ")");
                var it = new ToolStripMenuItem(nome) { Checked = serie == Cfg.Celular, Enabled = c[1] == "device" };
                it.Click += (s2, e2) => TrocarCelular(serie);
                miCel.DropDownItems.Add(it);
            }
        };
        var miAjuda = new ToolStripMenuItem("Como usar");
        miAjuda.Click += (s, e) => MessageBox.Show(
            "Mouse e teclado: leve o mouse para fora da borda da tela do lado em que o celular fica. " +
            "A setinha aparece no celular e o teclado passa a digitar nele. Para voltar, leve a setinha até a borda do celular " +
            "virada para o notebook.\n\n" +
            "Em \"Organizar telas\" você arrasta o celular para o lado certo e ajusta a velocidade.\n\n" +
            "\"Mostrar a tela do celular\" abre uma janela com a tela dele. Fechar essa janela não fecha o Italophone.\n\n" +
            "O som do celular toca no notebook enquanto o cabo estiver ligado.\n\n" +
            "A área de transferência é compartilhada: copiar em qualquer lado cola no outro. Enquanto o cabo está ligado, o celular usa um teclado vazio (sem Gboard) para poder ler a área de transferência dele; o Gboard volta sozinho ao desconectar.\n\n" +
            "Com mais de um celular no cabo, escolha qual usar em \"Celular\".\n\n" +
            "Com o mouse dentro do celular, uma combinação de teclas pode abrir um app ou executar uma ação nele; configure em \"Configurar atalhos...\". Vale qualquer tecla, inclusive as de volume e mídia do teclado.",
            "Italophone");
        var miSair = new ToolStripMenuItem("Sair");
        miSair.Click += (s, e) => Encerrar();
        menu.Items.AddRange(new ToolStripItem[] { miMouse, miMidia, miClip, miTela, miSom, new ToolStripSeparator(), miCel, miOrg, miAtalhos, miAjuda, miSair });
        ni.ContextMenuStrip = menu;
        ni.DoubleClick += (s, e) => Organizar();
        ni.Visible = true;
    }

    void AtualizarIcone()
    {
        string e;
        if (dentro) e = "dentro";
        else if (presente) e = "ok";
        else e = "esperando";
        if (e == estado) return;
        estado = e;
        switch (e)
        {
            case "dentro": ni.Icon = icDentro; ni.Text = "Italophone: mouse e teclado no celular"; break;
            case "ok": ni.Icon = icOk; ni.Text = "Italophone: celular conectado"; break;
            default: ni.Icon = icEsperando; ni.Text = "Italophone: esperando o cabo do celular"; break;
        }
    }

    // ---------------------------------------------------------------- ciclo (3 s)
    // celulares no cabo: numero de serie -> estado ("device", "unauthorized"...) e modelo
    List<string[]> ListarCelulares()
    {
        var l = new List<string[]>();
        foreach (var linha in Adb("devices -l", 4000).Split('\n'))
        {
            var p = linha.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[0] == "List") continue;
            string modelo = "";
            foreach (var x in p) if (x.StartsWith("model:")) modelo = x.Substring(6).Replace('_', ' ');
            l.Add(new[] { p[0], p[1], modelo });
        }
        return l;
    }

    bool avisouPermitir;

    // sem celular escolhido, fica com o primeiro que aparecer no cabo; avisa se falta permitir a depuracao
    // Interfaces de depuracao USB que o Windows ve no cabo agora (-1 = nao deu para consultar)
    static int InterfacesAdbNoUsb()
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass='AndroidUsbDeviceClass' OR Name LIKE '%ADB Interface%'"))
            using (var r = s.Get())
                return r.Count;
        }
        catch { return -1; }
    }

    // O servidor do adb as vezes trava e deixa de ver um celular que esta no cabo (comum quando
    // outro programa abre outra versao do adb). Se o Windows ve mais celulares do que o adb por
    // dois ciclos seguidos, reinicia o adb (no maximo uma vez por minuto).
    int ciclosAdbCego;
    DateTime proxReinicioAdb;
    List<string[]> ConferirAdb(List<string[]> l)
    {
        if (InterfacesAdbNoUsb() <= l.Count) { ciclosAdbCego = 0; return l; }
        if (++ciclosAdbCego < 2 || DateTime.Now < proxReinicioAdb) return l;
        ciclosAdbCego = 0;
        proxReinicioAdb = DateTime.Now.AddMinutes(1);
        Log("o adb nao ve um celular que esta no cabo; reiniciando o adb");
        Adb("kill-server", 5000);
        foreach (var p in Process.GetProcessesByName("adb"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
            p.Dispose();
        }
        Adb("start-server", 15000);
        return ListarCelulares();
    }

    void ProcurarCelular()
    {
        var l = ConferirAdb(ListarCelulares());
        if (Cfg.Celular == "")
        {
            foreach (var c in l)
                if (c[1] == "device")
                {
                    Cfg.Celular = c[0]; Cfg.Gravar();
                    Log("celular escolhido: " + c[0] + " " + c[2]);
                    return;
                }
        }
        bool pedindo = false;
        foreach (var c in l) if (c[1] == "unauthorized" && (Cfg.Celular == "" || Cfg.Celular == c[0])) pedindo = true;
        if (pedindo && !avisouPermitir)
        {
            avisouPermitir = true;
            ui.Post(_ => ni.ShowBalloonTip(10000, "Italophone", "Olhe a tela do celular e toque em \"Permitir\" (depuração USB). Marque \"Sempre permitir neste computador\".", ToolTipIcon.Info), null);
        }
        if (!pedindo) avisouPermitir = false;
    }

    // pedido do menu: quem troca e o ciclo (na linha dele), para nao brigar com uma conexao em andamento
    volatile string pedidoTroca;
    void TrocarCelular(string serie) { pedidoTroca = serie; Ciclo(); }

    void FazerTroca(string serie)
    {
        if (serie == Cfg.Celular) return;
        if (dentro) Sair_(-1);
        if (serv.Pronto || procServidor != null) DesligarServidor();
        if (borda.Pronto) borda.Fechar();
        Parar(som); Parar(espelho);
        appPreparado = false; jarEnviado = false; posConhecida = false;
        Cfg.Celular = serie; Cfg.Gravar();
        Log("celular trocado para " + serie);
    }

    // O ciclo usa o adb (que pode demorar, ex.: instalar o app no celular): roda fora da linha da
    // interface, um por vez; pedido feito durante um ciclo roda logo depois.
    int cicloRodando, cicloPendente;
    void Ciclo()
    {
        Interlocked.Exchange(ref cicloPendente, 1);
        if (Interlocked.CompareExchange(ref cicloRodando, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (Interlocked.Exchange(ref cicloPendente, 0) == 1)
                {
                    try { CicloTrabalho(); } catch (Exception ex) { Log("erro no ciclo: " + ex); }
                }
            }
            finally { Interlocked.Exchange(ref cicloRodando, 0); }
            // pedido que chegou entre o fim do laco e a liberacao
            if (Interlocked.CompareExchange(ref cicloPendente, 0, 0) == 1) Ciclo();
        });
    }

    void CicloTrabalho()
    {
        string troca = pedidoTroca;
        if (troca != null) { pedidoTroca = null; FazerTroca(troca); }
        if (Cfg.Celular == "" || !presente) ProcurarCelular();
        presente = Cfg.Celular != "" && Adb("-s " + Cfg.Celular + " get-state", 3000) == "device";
        bool? p = presente;
        Cuidar(som, ref p);
        Cuidar(espelho, ref p);

        if (presente && Cfg.Mouse)
        {
            if (!serv.Pronto && DateTime.Now >= proxServidor) LigarServidor();
            if (!borda.Pronto && DateTime.Now >= proxApp) LigarApp();
        }
        else
        {
            if (dentro) Sair_(-1);
            if (serv.Pronto || procServidor != null) DesligarServidor();
            if (borda.Pronto) borda.Fechar();
            if (!presente) { appPreparado = false; jarEnviado = false; }
        }
        ui.Post(_ => AtualizarIcone(), null);
    }

    void LigarServidor()
    {
        proxServidor = DateTime.Now.AddSeconds(10);
        try
        {
            if (!jarEnviado)
            {
                string jar = Path.Combine(Dir, "italophone-servidor.jar");
                string r = Adb("-s " + Cfg.Celular + " push \"" + jar + "\" " + JAR_CELULAR, 15000);
                if (!r.Contains("pushed")) { Log("falha ao enviar o servidor: " + r); return; }
                jarEnviado = true;
            }
            if (procServidor == null || procServidor.HasExited)
            {
                // um servidor que ficou de uma vez anterior prende o nome do soquete
                Adb("-s " + Cfg.Celular + " shell pkill -f br.italo.italophone.Servidor");
                var psi = new ProcessStartInfo(adb, "-s " + Cfg.Celular + " shell CLASSPATH=" + JAR_CELULAR + " app_process / br.italo.italophone.Servidor")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                procServidor = Process.Start(psi);
                procServidor.OutputDataReceived += (o, a) => { if (a.Data != null) Log("celular: " + a.Data); };
                procServidor.ErrorDataReceived += (o, a) => { if (a.Data != null) Log("celular!: " + a.Data); };
                procServidor.BeginOutputReadLine(); procServidor.BeginErrorReadLine();
                Thread.Sleep(800);
            }
            Adb("-s " + Cfg.Celular + " forward tcp:" + PORTA_SERVIDOR + " localabstract:italophone");
            for (int i = 0; i < 10 && !serv.Conectar(PORTA_SERVIDOR); i++) Thread.Sleep(300);
            if (serv.Pronto)
            {
                Log("servidor do celular conectado");
                // altura da barra de status, para o clique nela na tela espelhada
                try
                {
                    var mb = System.Text.RegularExpressions.Regex.Match(
                        Adb("-s " + Cfg.Celular + " shell \"dumpsys window | grep -m1 'type=statusBars frame'\""),
                        @"frame=\[\d+,\d+\]\[\d+,(\d+)\]");
                    if (mb.Success) barraPx = Math.Max(40, int.Parse(mb.Groups[1].Value));
                }
                catch { }
                proxServidor = DateTime.MinValue;
                // mouse novo do celular: a setinha nasce no meio da tela
                var g0 = geo;
                if (serv.Nasce == 'C' && g0 != null) { posConhecida = true; posAoLongo = g0.CompCel / 2; }
                else posConhecida = false;
            }
            else Log("servidor do celular nao respondeu");
        }
        catch (Exception ex) { Log("servidor: " + ex.Message); }
    }

    void DesligarServidor()
    {
        serv.Fechar();
        try { if (procServidor != null && !procServidor.HasExited) procServidor.Kill(); } catch { }
        procServidor = null;
    }

    bool avisouInstalar;

    // versionCode do app ja instalado no celular (0 = nao instalado)
    int VersaoAppNoCelular()
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            Adb("-s " + Cfg.Celular + " shell dumpsys package " + APP_CELULAR, 8000), @"versionCode=(\d+)");
        int v;
        return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : 0;
    }

    void LigarApp()
    {
        proxApp = DateTime.Now.AddSeconds(10);
        if (!appPreparado)
        {
            if (VersaoAppNoCelular() < VERSAO_APP)
            {
                string apk = Path.Combine(Dir, "Italophone.apk");
                if (!File.Exists(apk)) { Log("Italophone.apk nao encontrado"); return; }
                string r = Adb("-s " + Cfg.Celular + " install -r \"" + apk + "\"", 60000);
                Log("instalando o app no celular: " + r);
                if (!r.Contains("Success") && VersaoAppNoCelular() < VERSAO_APP)
                {
                    // tenta de novo daqui a 1 min (ex.: o usuario recusou a instalacao no celular)
                    proxApp = DateTime.Now.AddMinutes(1);
                    if (!avisouInstalar)
                    {
                        avisouInstalar = true;
                        ui.Post(_ => ni.ShowBalloonTip(15000, "Italophone", "Não consegui instalar o app do Italophone no celular. Se aparecer um pedido na tela do celular, aceite. Em celulares Xiaomi, Redmi e Poco, ligue \"Instalar via USB\" nas Opções do desenvolvedor.", ToolTipIcon.Warning), null);
                    }
                    return;
                }
            }
            Adb("-s " + Cfg.Celular + " shell appops set " + APP_CELULAR + " SYSTEM_ALERT_WINDOW allow");
            Adb("-s " + Cfg.Celular + " shell pm grant " + APP_CELULAR + " android.permission.POST_NOTIFICATIONS");
            // deixa o celular trocar de teclado por conta propria (ver Borda.kt), para ler a area
            // de transferencia dele; o proprio celular desfaz a troca ao desconectar. O "ime enable"
            // fica aqui (habilitar exige so o adb, nao precisa ser lido pelo app depois).
            Adb("-s " + Cfg.Celular + " shell pm grant " + APP_CELULAR + " android.permission.WRITE_SECURE_SETTINGS");
            Adb("-s " + Cfg.Celular + " shell ime enable " + APP_CELULAR + "/.ClipboardIme");
            appPreparado = true;
        }
        Adb("-s " + Cfg.Celular + " shell am start-foreground-service -n " + APP_CELULAR + "/.Borda");
        Adb("-s " + Cfg.Celular + " forward tcp:" + PORTA_APP + " tcp:" + PORTA_APP);
        Thread.Sleep(300);
        if (borda.Conectar(PORTA_APP)) { Log("app do celular conectado"); proxApp = DateTime.MinValue; }
    }

    void NovoTamanho(int w, int h)
    {
        Log("tela do celular " + w + "x" + h);
        posConhecida = false;   // girou: a posicao da setinha mudou
        if (Cfg.CelW != w || Cfg.CelH != h) { Cfg.CelW = w; Cfg.CelH = h; Cfg.Gravar(); }
        AtualizarGeometria();
    }

    public void AtualizarGeometria()
    {
        geo = Geometria.De(Cfg, Cfg.CelW, Cfg.CelH);
    }

    // ---------------------------------------------------------------- area de transferencia, nos dois sentidos
    // (ver Borda.kt: o celular vira o teclado padrao por conta propria para poder ler a dele).
    // clipUltimoEnviado guarda o ultimo texto trocado por qualquer lado, para nao ecoar de volta.
    void ClipboardMudouLocal()
    {
        if (!Cfg.Clip) return;
        string texto;
        try { if (!Clipboard.ContainsText()) return; texto = Clipboard.GetText(); }
        catch { return; }   // area de transferencia presa por outro programa nesse instante
        if (string.IsNullOrEmpty(texto) || texto == clipUltimoEnviado) return;
        clipUltimoEnviado = texto;
        if (borda.Pronto) borda.EnviarClip(texto);
    }

    void ClipDoCelularRecebido(string texto)
    {
        if (!Cfg.Clip || string.IsNullOrEmpty(texto) || texto == clipUltimoEnviado) return;
        clipUltimoEnviado = texto;
        try { Clipboard.SetText(texto); } catch { }
    }

    // ---------------------------------------------------------------- scrcpy
    void Iniciar(Scrcpy s)
    {
        string saida = Path.Combine(Dir, "scrcpy-" + s.Nome + ".txt");
        try
        {
            var psi = new ProcessStartInfo(scrcpyExe, s.Opcoes())
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.Combine(Dir, "scrcpy"),
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.EnvironmentVariables["ADB"] = adb;
            // sem isto o primeiro clique numa janela espelhada sem foco so a ativava e nao chegava
            // ao celular (ex.: clicar numa notificacao nao abria)
            psi.EnvironmentVariables["SDL_MOUSE_FOCUS_CLICKTHROUGH"] = "1";
            // icone do Italophone na janela da tela espelhada (e na barra de tarefas), no lugar do do scrcpy
            // (o scrcpy procura "scrcpy.png" e "disconnected.png" nesta pasta)
            string icones = Path.Combine(Dir, "icone");
            if (File.Exists(Path.Combine(icones, "scrcpy.png"))) psi.EnvironmentVariables["SCRCPY_ICON_DIR"] = icones;
            var w = new StreamWriter(saida, false, Encoding.UTF8) { AutoFlush = true };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (o, a) => { if (a.Data != null) lock (w) { try { w.WriteLine(a.Data); } catch { } } };
            p.OutputDataReceived += h; p.ErrorDataReceived += h;
            p.Exited += (o, a) => { lock (w) { try { w.Dispose(); } catch { } } };
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            s.Proc = p;
            s.Inicio = DateTime.Now;
            Log(s.Nome + ": iniciado");
        }
        catch (Exception ex) { Log(s.Nome + ": falha ao iniciar: " + ex.Message); s.Espera = DateTime.Now.AddSeconds(30); }
    }

    void Parar(Scrcpy s)
    {
        if (s.Vivo) { try { s.Proc.Kill(); s.Proc.WaitForExit(3000); } catch { } }
        s.Proc = null;
    }

    void Cuidar(Scrcpy s, ref bool? presente)
    {
        if (!s.Ligado) { if (s.Vivo) Parar(s); return; }
        if (s.Vivo) return;
        if (s.Proc != null)
        {
            double durou = (DateTime.Now - s.Inicio).TotalSeconds;
            Log(string.Format("{0}: saiu apos {1:0} s", s.Nome, durou));
            s.Proc = null;
            if (s.AoSair != null) s.AoSair();
            if (!s.Ligado) return;
            if (durou < 15) s.Espera = DateTime.Now.AddSeconds(20);
        }
        if (DateTime.Now < s.Espera || presente != true) return;
        Iniciar(s);
    }

    void AlternarTela()
    {
        espelho.Ligado = !espelho.Ligado;
        miTela.Checked = espelho.Ligado;
        espelho.Espera = DateTime.MinValue;
        Ciclo();   // o ciclo abre ou fecha a janela
    }

    // ---------------------------------------------------------------- organizar telas
    Organizador org;
    public void Organizar()
    {
        if (org != null && !org.IsDisposed) { org.Activate(); return; }
        org = new Organizador(this);
        org.Show();
        org.Activate();
    }

    public void AbrirPeloIniciar() { Organizar(); }

    // ---------------------------------------------------------------- configurar atalhos
    Atalhos atalhos;
    public void AbrirAtalhos()
    {
        if (atalhos != null && !atalhos.IsDisposed) { atalhos.Activate(); return; }
        atalhos = new Atalhos(this);
        atalhos.Show();
        atalhos.Activate();
    }

    // ---------------------------------------------------------------- ganchos (mouse e teclado)
    void IniciarGanchos()
    {
        procMouse = GanchoMouse;
        procTeclado = GanchoTeclado;
        ganchoThread = new Thread(() =>
        {
            ganchoTid = Nat.GetCurrentThreadId();
            IntPtr mod = Nat.GetModuleHandle(null);
            // handles locais: ao reinstalar, a linha velha solta so os dela
            IntPtr hm = Nat.SetWindowsHookEx(14, procMouse, mod, 0);
            IntPtr ht = Nat.SetWindowsHookEx(13, procTeclado, mod, 0);
            hMouse = hm; hTeclado = ht;
            if (hm == IntPtr.Zero || ht == IntPtr.Zero) Log("falha ao instalar os ganchos: " + Marshal.GetLastWin32Error());
            Application.Run();
            Nat.UnhookWindowsHookEx(hm);
            Nat.UnhookWindowsHookEx(ht);
        }) { IsBackground = true, Name = "ganchos", Priority = ThreadPriority.Highest };
        ganchoThread.SetApartmentState(ApartmentState.STA);
        ganchoThread.Start();
    }

    void ReinstalarGanchos()
    {
        int velho = ganchoTid;
        ganchoTid = 0;
        if (velho != 0) Nat.PostThreadMessage(velho, 0x12, IntPtr.Zero, IntPtr.Zero);   // a linha velha solta os ganchos dela
        Interlocked.Exchange(ref ultimoGancho, Environment.TickCount);
        IniciarGanchos();
    }

    // ---------------------------------------------------------------- barra de cima na tela espelhada
    // Com mouse de verdade no celular, clicar na barra de status abre as notificacoes. O scrcpy manda
    // o clique como toque de dedo, e toque na barra nao faz nada: aqui o clique ali vira "abrir as
    // notificacoes", como no celular.
    volatile int barraPx = 94;      // altura da barra de status do celular (px), lida ao conectar
    bool engolirSubida;

    bool CliqueBarraEspelho(int msg, Nat.MSLL m)
    {
        if (msg == 0x202)
        {
            if (!engolirSubida) return false;
            engolirSubida = false;
            return true;
        }
        engolirSubida = false;
        var proc = espelho.Proc;
        if (proc == null || !espelho.Vivo) return false;
        IntPtr h = Nat.GetAncestor(Nat.WindowFromPoint(m.pt), 2);   // GA_ROOT
        if (h == IntPtr.Zero) return false;
        int pid; Nat.GetWindowThreadProcessId(h, out pid);
        if (pid != proc.Id) return false;
        Nat.RECT r; if (!Nat.GetClientRect(h, out r) || r.Right <= 0 || r.Bottom <= 0) return false;
        var o = new Nat.POINT(); Nat.ClientToScreen(h, ref o);
        int w = borda.W > 0 ? borda.W : Cfg.CelW, a = borda.H > 0 ? borda.H : Cfg.CelH;
        // o scrcpy mostra a tela inteira do celular, centralizada e sem distorcer
        double esc = Math.Min(r.Right / (double)w, r.Bottom / (double)a);
        double ox = o.X + (r.Right - w * esc) / 2, oy = o.Y + (r.Bottom - a * esc) / 2;
        double px = (m.pt.X - ox) / esc, py = (m.pt.Y - oy) / esc;
        if (px < 0 || px >= w || py < 0 || py >= barraPx) return false;
        engolirSubida = true;
        ThreadPool.QueueUserWorkItem(_ => { try { Adb("-s " + Cfg.Celular + " shell cmd statusbar expand-notifications"); } catch { } });
        return true;
    }

    static bool BotaoApertado()
    {
        return (Nat.GetAsyncKeyState(0x01) < 0) || (Nat.GetAsyncKeyState(0x02) < 0) || (Nat.GetAsyncKeyState(0x04) < 0);
    }

    // shift/ctrl/alt/win, genericos (0x10/0x11/0x12/0x5B/0x5C) OU especificos de lado (0xA0-0xA5):
    // o teclado real manda o especifico (confirmado por log), por isso o gancho nunca trata esses
    // vk's como "tecla principal" de uma macro
    static bool EModificador(int vk)
    {
        return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C ||
               (vk >= 0xA0 && vk <= 0xA5);
    }

    IntPtr GanchoMouse(int code, IntPtr w, IntPtr l)
    {
        Interlocked.Exchange(ref ultimoGancho, Environment.TickCount);
        if (code < 0) return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
        int msg = (int)w;
        var m = (Nat.MSLL)Marshal.PtrToStructure(l, typeof(Nat.MSLL));
        try
        {
            if (dentro) { NoCelular(msg, m); return (IntPtr)1; }
            if ((msg == 0x201 || msg == 0x202) && CliqueBarraEspelho(msg, m)) return (IntPtr)1;
            if (msg == 0x200) VerBorda(m);
            if (dentro) return (IntPtr)1;
        }
        catch (Exception ex) { Log("gancho do mouse: " + ex.Message); }
        return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void VerBorda(Nat.MSLL m)
    {
        var g = geo;
        if (g == null || !Cfg.Mouse || !serv.Pronto) return;
        Nat.POINT cur;
        Nat.GetCursorPos(out cur);
        var M = g.Monitor;
        bool naBorda, empurra;
        int aoLongo = g.Vertical ? cur.Y : cur.X;
        switch (g.Lado)
        {
            case "esquerda": naBorda = cur.X <= M.Left; empurra = m.pt.X < M.Left; break;
            case "cima": naBorda = cur.Y <= M.Top; empurra = m.pt.Y < M.Top; break;
            case "baixo": naBorda = cur.Y >= M.Bottom - 1; empurra = m.pt.Y >= M.Bottom; break;
            default: naBorda = cur.X >= M.Right - 1; empurra = m.pt.X >= M.Right; break;
        }
        bool noMonitor = M.Contains(cur.X, cur.Y);
        if (!armado)
        {
            // so arma de novo depois de o mouse se afastar da borda
            int dist;
            switch (g.Lado)
            {
                case "esquerda": dist = cur.X - M.Left; break;
                case "cima": dist = cur.Y - M.Top; break;
                case "baixo": dist = M.Bottom - 1 - cur.Y; break;
                default: dist = M.Right - 1 - cur.X; break;
            }
            if (dist >= 4 || !noMonitor) armado = true;
            return;
        }
        if (!noMonitor || !naBorda || !empurra) return;
        if (aoLongo < g.SegIni || aoLongo >= g.SegFim) return;
        if (BotaoApertado()) return;
        Entrar(g, cur, aoLongo);
    }

    void Entrar(Geometria g, Nat.POINT cur, int aoLongo)
    {
        dentro = true;
        forcaFora = 0;
        ancora = cur;
        restoX = restoY = 0;
        botoes = 0; rodaAcc = rodaHAcc = 0;
        int alvo = (int)Math.Round((aoLongo - g.CelIniAoLongo) * g.K);
        alvo = Math.Max(0, Math.Min(g.CompCel - 1, alvo));
        int porta = g.Porta;
        // estimativa da setinha
        switch (porta)
        {
            case 0: estX = 0; estY = alvo; break;
            case 1: estX = g.CelW - 1; estY = alvo; break;
            case 2: estX = alvo; estY = 0; break;
            default: estX = alvo; estY = g.CelH - 1; break;
        }
        if (borda.Pronto) borda.Mostrar(porta);
        serv.Entrar(porta, alvo, posAoLongo, posConhecida);
        posConhecida = true; posAoLongo = alvo;
        ui.Post(_ => { capa.Cobrir(ancora.X, ancora.Y); AtualizarIcone(); }, null);
    }

    void NoCelular(int msg, Nat.MSLL m)
    {
        var g = geo;
        if (g == null || !serv.Pronto) { Sair_(-1); return; }
        switch (msg)
        {
            case 0x200:   // mover
            {
                int dx = m.pt.X - ancora.X, dy = m.pt.Y - ancora.Y;
                if (dx == 0 && dy == 0) return;
                double fx = dx * g.V + restoX, fy = dy * g.V + restoY;
                int ix = (int)Math.Round(fx), iy = (int)Math.Round(fy);
                restoX = fx - ix; restoY = fy - iy;
                // saindo pela borda do lado do notebook?
                bool paraFora;
                switch (g.Porta) { case 0: paraFora = dx < 0; break; case 1: paraFora = dx > 0; break; case 2: paraFora = dy < 0; break; default: paraFora = dy > 0; break; }
                if (paraFora)
                {
                    if (borda.Pronto && borda.NaBorda) { Sair_(borda.PosBorda); return; }
                    bool estNaPorta = g.Porta == 0 ? estX <= 0 : g.Porta == 1 ? estX >= g.CelW - 1 : g.Porta == 2 ? estY <= 0 : estY >= g.CelH - 1;
                    if (!borda.Pronto && estNaPorta) { Sair_(-2); return; }
                    // nos cantos (barra de status) a faixa invisivel nao pega: insistindo contra a borda, sai pela estimativa
                    if (estNaPorta) { forcaFora += Math.Abs(g.Vertical ? dx : dy); if (forcaFora > 60) { Sair_(-2); return; } }
                    else forcaFora = 0;
                }
                else forcaFora = 0;
                estX = Math.Max(0, Math.Min(g.CelW - 1, estX + ix));
                estY = Math.Max(0, Math.Min(g.CelH - 1, estY + iy));
                if (ix != 0 || iy != 0) serv.Mover(ix, iy);
                break;
            }
            case 0x201: botoes |= 1; serv.Botoes(botoes, 0, 0); break;
            case 0x202: botoes &= unchecked((byte)~1); serv.Botoes(botoes, 0, 0); break;
            case 0x204: botoes |= 2; serv.Botoes(botoes, 0, 0); break;
            case 0x205: botoes &= unchecked((byte)~2); serv.Botoes(botoes, 0, 0); break;
            case 0x207: botoes |= 4; serv.Botoes(botoes, 0, 0); break;
            case 0x208: botoes &= unchecked((byte)~4); serv.Botoes(botoes, 0, 0); break;
            case 0x20B:
            case 0x20C:
            {
                int qual = (m.mouseData >> 16) & 0xFFFF;
                byte bit = (byte)(qual == 1 ? 8 : 16);
                if (msg == 0x20B) botoes |= bit; else botoes &= (byte)~bit;
                serv.Botoes(botoes, 0, 0);
                break;
            }
            case 0x20A:   // roda
            {
                rodaAcc += (short)((m.mouseData >> 16) & 0xFFFF);
                int passos = rodaAcc / 120; rodaAcc -= passos * 120;
                if (passos != 0) serv.Botoes(botoes, passos, 0);
                break;
            }
            case 0x20E:   // roda horizontal
            {
                rodaHAcc += (short)((m.mouseData >> 16) & 0xFFFF);
                int passos = rodaHAcc / 120; rodaHAcc -= passos * 120;
                if (passos != 0) serv.Botoes(botoes, 0, passos);
                break;
            }
        }
    }

    // pos: posicao exata ao longo da borda do celular (px do celular); -1 = desconhecida; -2 = pela estimativa
    void Sair_(int pos)
    {
        if (!dentro) return;
        dentro = false;
        armado = false;
        var g = geo;
        // solta o que estiver apertado no celular
        if (botoes != 0) { botoes = 0; serv.Botoes(0, 0, 0); }
        if (teclasNoCelular.Count > 0 || relTeclado[0] != 0)
        {
            teclasNoCelular.Clear(); vkNoCelular.Clear(); Array.Clear(relTeclado, 0, 8);
            serv.Teclado(relTeclado);
        }
        serv.Midia(0);   // volume ou musica segurado ao sair: solta no celular
        if (borda.Pronto) borda.Mostrar(-1);
        serv.ApagarSetinha();

        int px = ancora.X, py = ancora.Y;
        if (g != null)
        {
            int aoLongoCel;
            if (pos >= 0) { aoLongoCel = pos; posConhecida = true; posAoLongo = pos; }
            else if (pos == -2) { aoLongoCel = (int)(g.Vertical ? estY : estX); posConhecida = false; }
            else { aoLongoCel = -1; posConhecida = false; }
            if (aoLongoCel >= 0)
            {
                int pc = g.CelIniAoLongo + (int)Math.Round(aoLongoCel / g.K);
                pc = Math.Max(g.SegIni, Math.Min(g.SegFim - 1, pc));
                if (g.Vertical) py = pc; else px = pc;
            }
            // o mouse do celular e desligado ao sair (a setinha some na hora); ao voltar ele nasce de novo
            if (serv.Nasce == 'C') { posConhecida = true; posAoLongo = g.CompCel / 2; }
            else if (serv.Nasce == 'U') posConhecida = false;
        }
        var a = new Nat.POINT { X = px, Y = py };
        ui.Post(_ => { capa.Descobrir(); Nat.SetCursorPos(a.X, a.Y); AtualizarIcone(); }, null);
    }

    IntPtr GanchoTeclado(int code, IntPtr w, IntPtr l)
    {
        if (code < 0) return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
        var k0 = (Nat.KBDLL)Marshal.PtrToStructure(l, typeof(Nat.KBDLL));
        // estado de ctrl/alt/shift/win: atualizado SEMPRE (mesmo fora do "dentro"), pra nao perder o
        // segurar se o mouse entra no celular com a tecla ja pressionada. GetAsyncKeyState do codigo
        // generico nao refletia de forma confiavel o especifico de lado que este teclado manda
        // (confirmado por log e teste isolado em 10/11), por isso o estado vem so daqui
        if (EModificador(k0.vk))
        {
            bool sol = (k0.flags & 0x80) != 0;
            bool v = !sol;
            if (k0.vk == 0x10 || (k0.vk >= 0xA0 && k0.vk <= 0xA1)) modShift = v;
            else if (k0.vk == 0x11 || (k0.vk >= 0xA2 && k0.vk <= 0xA3)) modCtrl = v;
            else if (k0.vk == 0x12 || (k0.vk >= 0xA4 && k0.vk <= 0xA5)) modAlt = v;
            else modWin = v;
        }
        if (!dentro) return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
        var k = k0;
        try
        {
            bool solta = (k.flags & 0x80) != 0;
            bool ext = (k.flags & 0x01) != 0;
            // o AltGr do ABNT2 manda um Ctrl falso junto (scan 0x21D): ignorar
            if ((k.scan & 0x200) != 0) return (IntPtr)1;
            // macro de teclado ("Configurar atalhos..." no menu, ou config.txt "macro=combo=acao"):
            // ctrl/alt/shift/win + uma tecla, inclusive as de volume/midia e as de atalho dedicadas.
            // Tem prioridade sobre o uso padrao delas. So olha teclas que nao sejam elas mesmas
            // modificador; o estado dos modificadores vem do que ja vimos passar pelo proprio gancho
            // (modCtrl/modAlt/modShift/modWin acima), nao de GetAsyncKeyState
            if (Cfg.Macros.Count > 0 && !EModificador(k.vk))
            {
                long chave = Config.ChaveCombo(modCtrl, modAlt, modShift, modWin, k.vk);
                string acao;
                if (Cfg.Macros.TryGetValue(chave, out acao))
                {
                    if (solta) teclasMacro.Remove(k.vk);
                    else if (teclasMacro.Add(k.vk)) DispararMacro(acao);   // Add==false: tecla repetindo ao segurar
                    return (IntPtr)1;
                }
            }
            // teclas de volume e musica sem macro: no celular (opcao do menu) ou no notebook
            if (k.vk >= 0xAD && k.vk <= 0xB3)
            {
                if (!Cfg.Midia) return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
                int[] usos = { 0xE2, 0xEA, 0xE9, 0xB5, 0xB6, 0xB7, 0xCD };   // mudo, volume -, volume +, proxima, anterior, parar, tocar/pausar
                serv.Midia(solta ? 0 : usos[k.vk - 0xAD]);
                return (IntPtr)1;
            }
            // teclas de atalho do teclado (Correio, Midia, App1, Calculadora) sem macro: seguem
            // normais no Windows (no HID nao fariam nada no celular mesmo)
            if (k.vk >= 0xB4 && k.vk <= 0xB7) return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);
            byte u = Teclas.Hid(k.vk, k.scan & 0xFF, ext);
            if (u == 0) return (IntPtr)1;
            if (solta && !vkNoCelular.Contains(k.vk) && !(u >= 0xE0 && (relTeclado[0] & (1 << (u - 0xE0))) != 0))
                return Nat.CallNextHookEx(IntPtr.Zero, code, w, l);   // tecla apertada antes de passar: solta no Windows
            if (u >= 0xE0)
            {
                byte bit = (byte)(1 << (u - 0xE0));
                if (solta) relTeclado[0] &= (byte)~bit; else relTeclado[0] |= bit;
            }
            else if (solta) teclasNoCelular.Remove(u);
            else if (!teclasNoCelular.Contains(u)) { if (teclasNoCelular.Count >= 6) teclasNoCelular.RemoveAt(0); teclasNoCelular.Add(u); }
            if (solta) vkNoCelular.Remove(k.vk); else vkNoCelular.Add(k.vk);
            for (int i = 0; i < 6; i++) relTeclado[2 + i] = i < teclasNoCelular.Count ? teclasNoCelular[i] : (byte)0;
            serv.Teclado(relTeclado);
        }
        catch (Exception ex) { Log("gancho do teclado: " + ex.Message); }
        return (IntPtr)1;
    }

    // ---------------------------------------------------------------- fim
    public void Encerrar()
    {
        if (dentro) Sair_(-1);
        ciclo.Stop();
        vigiaGancho.Stop();
        if (ganchoTid != 0) Nat.PostThreadMessage(ganchoTid, 0x12, IntPtr.Zero, IntPtr.Zero);
        borda.Fechar();
        DesligarServidor();
        Parar(som); Parar(espelho);
        Adb("-s " + Cfg.Celular + " forward --remove tcp:" + PORTA_SERVIDOR);
        Adb("-s " + Cfg.Celular + " forward --remove tcp:" + PORTA_APP);
        ni.Visible = false;
        Log("fechado");
        ExitThread();
    }
}

// As janelas do programa sao desenhadas a 96 dpi (100%). O projeto compila com o csc.exe cru, sem
// manifest/app.config, entao o WinForms classico NAO sabe que o processo e per-monitor-dpi-aware
// (isso so e levado em conta pelo AutoScaleMode.Dpi quando o app faz o "opt-in" novo do .NET
// Framework 4.7+ por manifest/config): sozinho, ele acha que o dpi "atual" e sempre 96 e nunca
// reescala nada, enquanto o Windows (porque o processo PEDIU per-monitor-v2 em SetProcessDpiAware-
// nessContext) desenha o texto (fonte em pontos) já no dpi real do monitor — daí o descompasso
// (texto maior, controles no lugar de sempre, cortando a janela). Essa base reescala a mao: uma vez
// ao abrir (OnLoad, quando o Windows ja decidiu em que monitor/dpi a janela vai aparecer) E de novo
// sempre que a janela for ARRASTADA pra um monitor de dpi diferente (WM_DPICHANGED, que so chega
// porque o processo e per-monitor-v2 — sem isso o `AutoScaleMode.Dpi` sozinho so cobre a abertura,
// nunca um arrasto ao vivo entre monitores).
class DialogDpi : Form
{
    [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr hWnd);
    const int WM_DPICHANGED = 0x02E0;

    int dpiAtual = 96;

    // desliga o autoscale proprio do WinForms (reagiria sozinho a troca de Font abaixo, escalando
    // tudo de novo por cima do que ja escalamos a mao)
    public DialogDpi() { AutoScaleMode = AutoScaleMode.None; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int dpi = GetDpiForWindow(Handle);
        if (dpi <= 0) dpi = 96;
        dpiAtual = dpi;
        float esc = dpi / 96f;
        if (Math.Abs(esc - 1f) < 0.01f) return;
        Font = new Font(Font.FontFamily, Font.Size * esc, Font.Style);   // os controles herdam (nenhum tem Font proprio)
        ClientSize = new Size((int)Math.Round(ClientSize.Width * esc), (int)Math.Round(ClientSize.Height * esc));
        ReescalarFilhos(Controls, esc);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            int novoDpi = (int)((long)m.WParam & 0xFFFF);
            float esc = novoDpi / (float)dpiAtual;
            if (Math.Abs(esc - 1f) > 0.01f)
            {
                Font = new Font(Font.FontFamily, Font.Size * esc, Font.Style);
                ReescalarFilhos(Controls, esc);
                var r = (Nat.RECT)Marshal.PtrToStructure(m.LParam, typeof(Nat.RECT));
                Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);   // retangulo que o Windows ja sugere pro dpi novo
            }
            dpiAtual = novoDpi;
        }
        base.WndProc(ref m);
    }

    static void ReescalarFilhos(Control.ControlCollection cs, float esc)
    {
        foreach (Control c in cs)
        {
            c.Location = new Point((int)Math.Round(c.Location.X * esc), (int)Math.Round(c.Location.Y * esc));
            c.Size = new Size((int)Math.Round(c.Size.Width * esc), (int)Math.Round(c.Size.Height * esc));
            if (c.Controls.Count > 0) ReescalarFilhos(c.Controls, esc);
        }
    }
}

// ===================================================================== janela "Organizar telas"
class Organizador : DialogDpi
{
    readonly App app;
    readonly Panel area;
    readonly TrackBar vel;
    readonly Label dica;
    Rectangle[] telas;
    Rectangle total;
    double esc;            // px da tela do Windows -> px do desenho
    Rectangle cel;         // celular em px do Windows
    string lado; Rectangle monitor;
    bool arrastando; Point inicioArr; Rectangle celInicio;

    public Organizador(App app)
    {
        this.app = app;
        Text = "Italophone - Organizar telas";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(760, 560);
        BackColor = Color.White;

        dica = new Label
        {
            Text = "Arraste o celular para o lado da tela em que ele fica. O mouse passa para o celular por esse trecho da borda.",
            Location = new Point(16, 10), AutoSize = true, MaximumSize = new Size(728, 0)
        };
        area = new DoubleBufferedPanel { Location = new Point(16, 60), Size = new Size(728, 380), BackColor = Color.FromArgb(243, 243, 243) };
        area.Paint += Desenhar;
        area.MouseDown += (s, e) => { if (Desenho(cel).Contains(e.Location)) { arrastando = true; inicioArr = e.Location; celInicio = cel; } };
        area.MouseMove += (s, e) =>
        {
            area.Cursor = Desenho(cel).Contains(e.Location) || arrastando ? Cursors.SizeAll : Cursors.Default;
            if (!arrastando) return;
            int dx = (int)((e.X - inicioArr.X) / esc), dy = (int)((e.Y - inicioArr.Y) / esc);
            cel = new Rectangle(celInicio.X + dx, celInicio.Y + dy, cel.Width, cel.Height);
            area.Invalidate();
        };
        area.MouseUp += (s, e) => { if (arrastando) { arrastando = false; Encaixar(); area.Invalidate(); } };

        var lv = new Label { Text = "Velocidade do mouse no celular", Location = new Point(16, 452), AutoSize = true };
        vel = new TrackBar { Location = new Point(16, 476), Size = new Size(420, 45), Minimum = 5, Maximum = 50, TickFrequency = 5 };
        var lDev = new Label { Text = "devagar", Location = new Point(20, 512), AutoSize = true, ForeColor = Color.DimGray };
        var lRap = new Label { Text = "rápido", Location = new Point(392, 512), AutoSize = true, ForeColor = Color.DimGray };

        var ok = new Button { Text = "Salvar", Location = new Point(552, 500), Size = new Size(90, 34) };
        var cancelar = new Button { Text = "Cancelar", Location = new Point(654, 500), Size = new Size(90, 34) };
        ok.Click += (s, e) => Salvar();
        cancelar.Click += (s, e) => Close();
        AcceptButton = ok; CancelButton = cancelar;
        Controls.AddRange(new Control[] { dica, area, lv, vel, lDev, lRap, ok, cancelar });

        Carregar();
    }

    class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; } }

    void Carregar()
    {
        var c = app.Cfg;
        var scr = Screen.AllScreens;
        telas = new Rectangle[scr.Length];
        for (int i = 0; i < scr.Length; i++) telas[i] = scr[i].Bounds;
        var g = Geometria.De(c, c.CelW, c.CelH);
        monitor = g.Monitor; lado = g.Lado; cel = g.Cel;
        vel.Value = Math.Max(vel.Minimum, Math.Min(vel.Maximum, (int)Math.Round(g.V * 10)));
        Escala();
    }

    void Escala()
    {
        total = cel;
        foreach (var t in telas) total = Rectangle.Union(total, t);
        total.Inflate(total.Width / 8 + 200, total.Height / 8 + 200);
        esc = Math.Min(area.Width / (double)total.Width, area.Height / (double)total.Height);
    }

    Rectangle Desenho(Rectangle r)
    {
        double ox = (area.Width - total.Width * esc) / 2, oy = (area.Height - total.Height * esc) / 2;
        return new Rectangle((int)(ox + (r.X - total.X) * esc), (int)(oy + (r.Y - total.Y) * esc), (int)(r.Width * esc), (int)(r.Height * esc));
    }

    // encosta o celular na borda livre mais proxima de alguma tela
    void Encaixar()
    {
        double melhor = double.MaxValue;
        Rectangle mel = cel; string melLado = lado; Rectangle melMon = monitor;
        var centro = new Point(cel.X + cel.Width / 2, cel.Y + cel.Height / 2);
        foreach (var t in telas)
        {
            foreach (var ld in new[] { "direita", "esquerda", "cima", "baixo" })
            {
                bool vertical = ld == "direita" || ld == "esquerda";
                int ini = vertical ? cel.Y : cel.X;
                // pelo menos um terco do celular encostado na tela
                int comp = vertical ? cel.Height : cel.Width;
                int minIni = (vertical ? t.Top : t.Left) - comp * 2 / 3, maxIni = (vertical ? t.Bottom : t.Right) - comp / 3;
                ini = Math.Max(minIni, Math.Min(maxIni, ini));
                var r = Geometria.RetCelular(t, ld, ini, cel.Width, cel.Height);
                bool livre = true;
                foreach (var o in telas)
                {
                    var faixa = r;
                    if (o.IntersectsWith(faixa)) { livre = false; break; }
                    // outra tela colada no mesmo trecho da borda
                    Rectangle borda;
                    switch (ld)
                    {
                        case "direita": borda = new Rectangle(t.Right, Math.Max(t.Top, r.Top), 1, Math.Max(1, Math.Min(t.Bottom, r.Bottom) - Math.Max(t.Top, r.Top))); break;
                        case "esquerda": borda = new Rectangle(t.Left - 1, Math.Max(t.Top, r.Top), 1, Math.Max(1, Math.Min(t.Bottom, r.Bottom) - Math.Max(t.Top, r.Top))); break;
                        case "cima": borda = new Rectangle(Math.Max(t.Left, r.Left), t.Top - 1, Math.Max(1, Math.Min(t.Right, r.Right) - Math.Max(t.Left, r.Left)), 1); break;
                        default: borda = new Rectangle(Math.Max(t.Left, r.Left), t.Bottom, Math.Max(1, Math.Min(t.Right, r.Right) - Math.Max(t.Left, r.Left)), 1); break;
                    }
                    if (o != t && o.IntersectsWith(borda)) { livre = false; break; }
                }
                if (!livre) continue;
                double d = Math.Pow(r.X + r.Width / 2 - centro.X, 2) + Math.Pow(r.Y + r.Height / 2 - centro.Y, 2);
                if (d < melhor) { melhor = d; mel = r; melLado = ld; melMon = t; }
            }
        }
        cel = mel; lado = melLado; monitor = melMon;
        Escala();
    }

    void Desenhar(object s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // usa so a lista guardada ao abrir (um monitor desligado com a janela aberta nao quebra o desenho)
        var principal = Screen.PrimaryScreen.Bounds;
        using (var fTela = new SolidBrush(Color.FromArgb(0, 103, 192)))
        using (var fSel = new SolidBrush(Color.FromArgb(0, 84, 166)))
        using (var txt = new SolidBrush(Color.White))
        using (var fonte = new Font("Segoe UI", 11f, FontStyle.Bold))
        using (var fonteP = new Font("Segoe UI", 8.5f))
        {
            for (int i = 0; i < telas.Length; i++)
            {
                var r = Desenho(telas[i]);
                r.Inflate(-2, -2);
                g.FillRectangle(telas[i] == monitor ? fSel : fTela, r);
                string nome = (i + 1).ToString();
                var sz = g.MeasureString(nome, fonte);
                g.DrawString(nome, fonte, txt, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
                if (telas[i] == principal) g.DrawString("principal", fonteP, txt, r.X + 6, r.Bottom - 20);
            }
            // celular: retangulo arredondado escuro com "tela"
            var c = Desenho(cel);
            using (var gp = Arredondado(c, Math.Max(4, Math.Min(c.Width, c.Height) / 6)))
            using (var fc = new SolidBrush(Color.FromArgb(32, 32, 32)))
                g.FillPath(fc, gp);
            var tela = Rectangle.Inflate(c, -Math.Max(2, c.Width / 12), -Math.Max(3, c.Height / 14));
            using (var ft = new SolidBrush(Color.FromArgb(236, 112, 0))) g.FillRectangle(ft, tela);
            var nc = "celular";
            var szc = g.MeasureString(nc, fonteP);
            g.DrawString(nc, fonteP, txt, c.X + (c.Width - szc.Width) / 2, c.Y + (c.Height - szc.Height) / 2);
            // trecho da borda por onde o mouse passa
            var geo = new Geometria { Monitor = monitor, Lado = lado, Cel = cel };
            bool vertical = lado == "direita" || lado == "esquerda";
            int ini = vertical ? Math.Max(monitor.Top, cel.Top) : Math.Max(monitor.Left, cel.Left);
            int fim = vertical ? Math.Min(monitor.Bottom, cel.Bottom) : Math.Min(monitor.Right, cel.Right);
            if (fim > ini)
            {
                Rectangle b;
                switch (lado)
                {
                    case "esquerda": b = new Rectangle(monitor.Left, ini, 1, fim - ini); break;
                    case "cima": b = new Rectangle(ini, monitor.Top, fim - ini, 1); break;
                    case "baixo": b = new Rectangle(ini, monitor.Bottom - 1, fim - ini, 1); break;
                    default: b = new Rectangle(monitor.Right - 1, ini, 1, fim - ini); break;
                }
                var bd = Desenho(b);
                bd.Inflate(vertical ? 3 : 0, vertical ? 0 : 3);
                using (var fb = new SolidBrush(Color.FromArgb(46, 200, 90))) g.FillRectangle(fb, bd);
            }
        }
    }

    static GraphicsPath Arredondado(Rectangle r, int raio)
    {
        var gp = new GraphicsPath();
        int d = raio * 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    void Salvar()
    {
        var c = app.Cfg;
        c.Lado = lado;
        c.Monitor = monitor;
        c.Inicio = (lado == "direita" || lado == "esquerda") ? cel.Y : cel.X;
        c.Velocidade = vel.Value / 10.0;
        c.Gravar();
        app.AtualizarGeometria();
        app.Log("telas organizadas: " + lado + " de " + monitor + ", inicio " + c.Inicio + ", velocidade " + c.Velocidade);
        Close();
    }
}

// ===================================================================== nome bonito do app (Play Store)
// O Android (sem root/aapt) nao devolve o nome legivel de um pacote por adb, so classe/pacote. Em
// vez de mostrar "com.nvt.cs" no "Escolher instalado...", busca o titulo da pagina publica do app
// na Play Store pelo id do pacote (nenhum dado do Ítalo entra nisso, só o pacote); sem internet ou
// sem pagina na Play Store, mostra o pacote mesmo. Guarda em cache (por pacote) para não repetir.
static class NomesApp
{
    static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
    static readonly HttpClient http = CriarHttp();
    static string arquivo;

    static HttpClient CriarHttp()
    {
        var h = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Italophone)");
        return c;
    }

    // le o cache em disco (pacote=nome, um por linha) ao abrir o programa, pra nunca mais buscar de
    // novo um app ja conhecido de uma vez anterior
    public static void Inicializar(string arq)
    {
        arquivo = arq;
        try
        {
            if (!File.Exists(arq)) return;
            foreach (var l in File.ReadAllLines(arq))
            {
                var p = l.Split(new[] { '=' }, 2);
                if (p.Length == 2 && p[0].Trim() != "") cache[p[0].Trim()] = p[1].Trim();
            }
        }
        catch { }
    }

    static void Salvar()
    {
        if (arquivo == null) return;
        try
        {
            var sb = new StringBuilder();
            lock (cache) { foreach (var kv in cache) sb.AppendLine(kv.Key + "=" + kv.Value); }
            File.WriteAllText(arquivo, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    public static async Task<string> Buscar(string pacote)
    {
        lock (cache) { string v; if (cache.TryGetValue(pacote, out v)) return v; }
        string nome = pacote;
        try
        {
            string html = await http.GetStringAsync("https://play.google.com/store/apps/details?id=" + Uri.EscapeDataString(pacote) + "&hl=pt_BR&gl=BR");
            var m = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.Singleline);
            if (m.Success)
            {
                string t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                foreach (var sufixo in new[] { " - Apps no Google Play", " – Apps no Google Play", " - Apps on Google Play", " – Apps on Google Play" })
                {
                    int i = t.IndexOf(sufixo, StringComparison.OrdinalIgnoreCase);
                    if (i > 0) { t = t.Substring(0, i); break; }
                }
                if (t.Trim() != "") nome = t.Trim();
            }
        }
        catch { }
        lock (cache) { cache[pacote] = nome; }
        if (nome != pacote) Salvar();   // so grava em disco o que achou de verdade; "nao achou" fica so na memoria, pra tentar de novo na proxima vez
        return nome;
    }
}

// ===================================================================== configurar atalhos
class Atalhos : DialogDpi
{
    readonly App app;
    readonly ListView lista;

    public Atalhos(App app)
    {
        this.app = app;
        Text = "Italophone - Atalhos de teclado";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(520, 420);
        BackColor = Color.White;

        string textoDica = "Com o mouse dentro do celular, a combinação de teclas abre o app ou executa a ação escolhida.";
        var dica = new Label { Text = textoDica, Location = new Point(16, 10), AutoSize = true, MaximumSize = new Size(488, 0) };
        // a dica quebra em 2 linhas: "dica.Bottom" nao da a altura certa antes do controle estar na
        // janela (o AutoSize so mede direito depois); mede a mao pra calcular onde a lista comeca
        int alturaDica = TextRenderer.MeasureText(textoDica, Font, new Size(488, 0), TextFormatFlags.WordBreak).Height;
        int yLista = 10 + alturaDica + 12;
        lista = new ListView
        {
            Location = new Point(16, yLista), Size = new Size(488, 322 - yLista),
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false
        };
        lista.Columns.Add("Teclas", 130);
        lista.Columns.Add("Ação", 340);
        lista.DoubleClick += (s, e) => Editar();

        var add = new Button { Text = "Adicionar...", Location = new Point(16, 332), Size = new Size(110, 32) };
        var editar = new Button { Text = "Editar...", Location = new Point(134, 332), Size = new Size(100, 32) };
        var remover = new Button { Text = "Remover", Location = new Point(242, 332), Size = new Size(100, 32) };
        add.Click += (s, e) => Adicionar();
        editar.Click += (s, e) => Editar();
        remover.Click += (s, e) => Remover();

        var fechar = new Button { Text = "Fechar", Location = new Point(414, 372), Size = new Size(90, 32) };
        fechar.Click += (s, e) => Close();
        CancelButton = fechar;

        Controls.AddRange(new Control[] { dica, lista, add, editar, remover, fechar });
        Carregar();
    }

    void Carregar()
    {
        lista.Items.Clear();
        foreach (var m in app.Cfg.Lista)
        {
            int idxAcao = Array.IndexOf(App.AcoesFixas, m.Acao);
            string texto = idxAcao >= 0 ? App.AcoesFixasTexto[idxAcao] : m.Acao;
            var it = new ListViewItem(new[] { m.Combo, texto });
            it.Tag = m;
            lista.Items.Add(it);
            if (idxAcao < 0) ResolverNomeNaLista(it, m.Acao);   // pacote Android: busca o nome bonito em segundo plano
        }
    }

    async void ResolverNomeNaLista(ListViewItem it, string pacote)
    {
        string nome = await NomesApp.Buscar(pacote);
        if (IsDisposed || nome == pacote || it.ListView == null) return;
        it.SubItems[1].Text = nome + "  (" + pacote + ")";
    }

    void Adicionar()
    {
        var dlg = new NovoAtalho(app, null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        app.Cfg.Lista.Add(dlg.Resultado);
        app.Cfg.ReconstruirMacros();
        app.Cfg.Gravar();
        Carregar();
    }

    void Editar()
    {
        if (lista.SelectedItems.Count == 0) return;
        var m = (Config.ItemMacro)lista.SelectedItems[0].Tag;
        var dlg = new NovoAtalho(app, m);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        m.Combo = dlg.Resultado.Combo; m.Acao = dlg.Resultado.Acao;
        app.Cfg.ReconstruirMacros();
        app.Cfg.Gravar();
        Carregar();
    }

    void Remover()
    {
        if (lista.SelectedItems.Count == 0) return;
        var m = (Config.ItemMacro)lista.SelectedItems[0].Tag;
        app.Cfg.Lista.Remove(m);
        app.Cfg.ReconstruirMacros();
        app.Cfg.Gravar();
        Carregar();
    }
}

class NovoAtalho : DialogDpi
{
    readonly App app;
    readonly TextBox txtCombo, txtPacote;
    readonly ComboBox cboAcao;
    readonly Button btnEscolherApp;
    readonly Label lblNomeApp;
    readonly System.Windows.Forms.Timer timerNome = new System.Windows.Forms.Timer();
    public Config.ItemMacro Resultado;

    static readonly string[] AcoesFixas = App.AcoesFixas;
    static readonly string[] AcoesTexto = App.AcoesFixasTexto;

    public NovoAtalho(App app, Config.ItemMacro existente)
    {
        this.app = app;
        Text = existente == null ? "Italophone - Novo atalho" : "Italophone - Editar atalho";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 272);
        BackColor = Color.White;

        var l1 = new Label { Text = "Teclas (clique na caixa e pressione a combinação):", Location = new Point(16, 14), AutoSize = true };
        txtCombo = new TextBox { Location = new Point(16, 38), Size = new Size(388, 26), ReadOnly = true, Text = existente != null ? existente.Combo : "" };
        txtCombo.KeyDown += Capturar;

        var l2 = new Label { Text = "Ação:", Location = new Point(16, 78), AutoSize = true };
        cboAcao = new ComboBox { Location = new Point(16, 100), Size = new Size(388, 26), DropDownStyle = ComboBoxStyle.DropDownList };
        cboAcao.Items.Add("Abrir um app do celular");
        cboAcao.Items.AddRange(AcoesTexto);
        cboAcao.SelectedIndexChanged += (s, e) => AtualizarCampoPacote();

        txtPacote = new TextBox { Location = new Point(16, 134), Size = new Size(280, 26) };
        btnEscolherApp = new Button { Text = "Escolher instalado...", Location = new Point(304, 133), Size = new Size(100, 28) };
        btnEscolherApp.Click += (s, e) => EscolherApp();
        lblNomeApp = new Label { Location = new Point(16, 164), AutoSize = true, MaximumSize = new Size(388, 0), ForeColor = Color.DimGray };
        txtPacote.TextChanged += (s, e) => { timerNome.Stop(); timerNome.Start(); };
        timerNome.Interval = 500;
        timerNome.Tick += async (s, e) => { timerNome.Stop(); await AtualizarNomeApp(); };

        var ok = new Button { Text = "Salvar", Location = new Point(224, 218), Size = new Size(90, 32) };
        var cancelar = new Button { Text = "Cancelar", Location = new Point(320, 218), Size = new Size(84, 32) };
        ok.Click += (s, e) => Salvar();
        cancelar.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok; CancelButton = cancelar;

        Controls.AddRange(new Control[] { l1, txtCombo, l2, cboAcao, txtPacote, btnEscolherApp, lblNomeApp, ok, cancelar });
        if (existente != null && Array.IndexOf(AcoesFixas, existente.Acao) < 0) AtualizarNomeApp();

        if (existente != null)
        {
            int idxAcao = Array.IndexOf(AcoesFixas, existente.Acao);
            if (idxAcao >= 0) cboAcao.SelectedIndex = idxAcao + 1;
            else { cboAcao.SelectedIndex = 0; txtPacote.Text = existente.Acao; }
        }
        else cboAcao.SelectedIndex = 0;
        AtualizarCampoPacote();
    }

    // captura a combinacao pressionada direto na caixa (nao precisa saber o nome da tecla: o
    // Windows manda o mesmo codigo tanto para letras/F/setas quanto para volume, midia e as
    // teclas de atalho dedicadas do teclado)
    void Capturar(object sender, KeyEventArgs e)
    {
        e.Handled = true; e.SuppressKeyPress = true;
        int vk = (int)e.KeyCode;
        if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C) return;   // so o modificador: espera a tecla principal
        bool win = Nat.GetAsyncKeyState(0x5B) < 0 || Nat.GetAsyncKeyState(0x5C) < 0;
        txtCombo.Text = Config.TextoCombo(e.Control, e.Alt, e.Shift, win, vk);
    }

    void AtualizarCampoPacote()
    {
        bool abrirApp = cboAcao.SelectedIndex == 0;
        txtPacote.Visible = abrirApp;
        btnEscolherApp.Visible = abrirApp;
        lblNomeApp.Visible = abrirApp;
        if (abrirApp) AtualizarNomeApp();
    }

    // mostra embaixo da caixa do pacote o nome do app (Play Store), se achar
    async Task AtualizarNomeApp()
    {
        string pacote = txtPacote.Text.Trim();
        if (pacote == "" || !pacote.Contains(".")) { lblNomeApp.Text = ""; return; }
        lblNomeApp.Text = "procurando o nome do app...";
        string nome = await NomesApp.Buscar(pacote);
        if (IsDisposed || txtPacote.Text.Trim() != pacote) return;
        lblNomeApp.Text = nome == pacote ? "" : "app: " + nome;
    }

    async void EscolherApp()
    {
        btnEscolherApp.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            string saida = await Task.Run(() => app.Adb("-s " + app.Cfg.Celular + " shell pm list packages -3", 8000));
            var pacotes = new List<string>();
            foreach (var linha in saida.Split('\n'))
            {
                var l = linha.Trim();
                if (l.StartsWith("package:")) pacotes.Add(l.Substring(8).Trim());
            }
            if (pacotes.Count == 0) { MessageBox.Show(this, "Não encontrei apps instalados no celular (ele está no cabo?).", "Italophone"); return; }
            var itens = new List<ItemApp>();
            using (var sem = new SemaphoreSlim(8))
            {
                var tarefas = pacotes.Select(async p =>
                {
                    await sem.WaitAsync();
                    try { return new ItemApp { Nome = await NomesApp.Buscar(p), Pacote = p }; }
                    finally { sem.Release(); }
                }).ToList();
                itens.AddRange(await Task.WhenAll(tarefas));
            }
            itens.Sort((a, b) => string.Compare(a.Nome, b.Nome, StringComparison.OrdinalIgnoreCase));
            using (var dlg = new EscolherDaLista("Escolher app", itens))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) txtPacote.Text = dlg.Escolhido;
            }
        }
        finally
        {
            Cursor = Cursors.Default;
            btnEscolherApp.Enabled = true;
        }
    }

    void Salvar()
    {
        if (txtCombo.Text.Trim() == "") { MessageBox.Show(this, "Pressione a combinação de teclas desse atalho.", "Italophone"); return; }
        long chave;
        if (!Config.AnalisarCombo(txtCombo.Text, out chave)) { MessageBox.Show(this, "Combinação inválida.", "Italophone"); return; }
        string acao = cboAcao.SelectedIndex == 0 ? txtPacote.Text.Trim() : AcoesFixas[cboAcao.SelectedIndex - 1];
        if (acao == "") { MessageBox.Show(this, "Informe o pacote do app a abrir.", "Italophone"); return; }
        Resultado = new Config.ItemMacro { Combo = txtCombo.Text, Acao = acao };
        DialogResult = DialogResult.OK;
        Close();
    }
}

// um app instalado no celular: nome bonito (Play Store, ou o proprio pacote se nao achar) + pacote
class ItemApp
{
    public string Nome, Pacote;
    public override string ToString() { return Nome == Pacote ? Pacote : Nome + "  (" + Pacote + ")"; }
}

class EscolherDaLista : DialogDpi
{
    readonly ListBox lb;
    public string Escolhido;

    public EscolherDaLista(string titulo, List<ItemApp> itens)
    {
        Text = "Italophone - " + titulo;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(440, 420);
        BackColor = Color.White;

        lb = new ListBox { Location = new Point(16, 16), Size = new Size(408, 348) };
        lb.Items.AddRange(itens.ToArray());
        lb.DoubleClick += (s, e) => Escolher();

        var ok = new Button { Text = "Escolher", Location = new Point(250, 376), Size = new Size(90, 32) };
        var cancelar = new Button { Text = "Cancelar", Location = new Point(344, 376), Size = new Size(80, 32) };
        ok.Click += (s, e) => Escolher();
        cancelar.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok; CancelButton = cancelar;

        Controls.AddRange(new Control[] { lb, ok, cancelar });
    }

    void Escolher()
    {
        var item = lb.SelectedItem as ItemApp;
        if (item == null) return;
        Escolhido = item.Pacote;
        DialogResult = DialogResult.OK;
        Close();
    }
}

static class Programa
{
    [STAThread]
    static void Main()
    {
        bool novo;
        using (var mutex = new Mutex(true, @"Local\Italophone", out novo))
        using (var abrir = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Italophone-abrir"))
        {
            if (!novo) { abrir.Set(); return; }
            if (!Nat.SetProcessDpiAwarenessContext(new IntPtr(-4))) Nat.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var app = new App();
            var ctx = SynchronizationContext.Current;
            var t = new Thread(() => { while (true) { abrir.WaitOne(); ctx.Post(_ => app.AbrirPeloIniciar(), null); } }) { IsBackground = true };
            t.Start();
            Application.Run(app);
        }
    }
}
