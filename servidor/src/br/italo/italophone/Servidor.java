package br.italo.italophone;

import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.SystemClock;
import android.system.ErrnoException;
import android.system.Os;
import android.system.OsConstants;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;

import java.io.DataInputStream;
import java.io.FileDescriptor;
import java.io.IOException;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;

/**
 * Roda no celular como usuario "shell" (adb), igual ao servidor do scrcpy:
 *   CLASSPATH=/data/local/tmp/italophone-servidor.jar app_process / br.italo.italophone.Servidor
 *
 * Cria um mouse e um teclado de verdade pelo /dev/uhid. O Android trata como se fossem
 * aparelhos ligados no celular: a setinha e a do proprio sistema.
 *
 * O notebook conecta em localabstract:italophone e manda mensagens binarias:
 *   'M' botoes(u8) dx(i16 LE) dy(i16 LE) roda(i8) rodaH(i8)   movimento do mouse
 *   'K' relatorio de teclado (8 bytes)
 *   'C' relatorio de teclas de midia (2 bytes, uso do HID Consumer)
 *   'W'                                                 acorda a tela
 *   'P'                                                 apaga a setinha (desliga o mouse virtual)
 *   'Q'                                                 encerra
 */
public final class Servidor {
    private static final int UHID_DESTROY = 1, UHID_CREATE2 = 11, UHID_INPUT2 = 12;
    private static final short BUS_VIRTUAL = 0x06;

    private static final byte[] DESC_MOUSE = bytes(
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05, 0x75, 0x01, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x03, 0x81, 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x81, 0x06,
        0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06,
        0x05, 0x0C, 0x0A, 0x38, 0x02, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06,
        0xC0, 0xC0);

    // teclado padrao (com as teclas internacionais do ABNT2) + LEDs
    private static final byte[] DESC_TECLADO = bytes(
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
        0x95, 0x05, 0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02,
        0x95, 0x01, 0x75, 0x03, 0x91, 0x01,
        0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x05, 0x07, 0x19, 0x00, 0x2A, 0xFF, 0x00, 0x81, 0x00,
        0xC0);

    // teclas de midia (volume, tocar/pausar, proxima...)
    private static final byte[] DESC_MIDIA = bytes(
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01,
        0x15, 0x00, 0x26, 0xFF, 0x03, 0x19, 0x00, 0x2A, 0xFF, 0x03, 0x75, 0x10, 0x95, 0x01, 0x81, 0x00,
        0xC0);

    private static byte[] bytes(int... v) {
        byte[] b = new byte[v.length];
        for (int i = 0; i < v.length; i++) b[i] = (byte) v[i];
        return b;
    }

    /** Um aparelho UHID (um arquivo /dev/uhid aberto para cada um). */
    static final class Uhid {
        final FileDescriptor fd;

        Uhid(String nome, int produto, byte[] desc) throws ErrnoException, IOException {
            fd = Os.open("/dev/uhid", OsConstants.O_RDWR, 0);
            ByteBuffer b = ByteBuffer.allocate(280 + desc.length).order(ByteOrder.nativeOrder());
            b.putInt(UHID_CREATE2);
            byte[] n = nome.getBytes(StandardCharsets.UTF_8);
            b.put(n, 0, Math.min(n.length, 127));
            b.put(new byte[256 - Math.min(n.length, 127)]);
            b.putShort((short) desc.length);
            b.putShort(BUS_VIRTUAL);
            b.putInt(0x18D1);   // fornecedor generico
            b.putInt(produto);
            b.putInt(0);
            b.putInt(0);
            b.put(desc);
            escrever(b.array());
            // o kernel manda avisos (inicio, LEDs); e preciso ler para a fila nao encher
            Thread t = new Thread(() -> {
                byte[] lixo = new byte[4380];
                try { while (true) { if (Os.read(fd, lixo, 0, lixo.length) <= 0) break; } } catch (Exception ignored) { }
            });
            t.setDaemon(true);
            t.start();
        }

        void enviar(byte[] dados, int len) {
            ByteBuffer b = ByteBuffer.allocate(6 + len).order(ByteOrder.nativeOrder());
            b.putInt(UHID_INPUT2);
            b.putShort((short) len);
            b.put(dados, 0, len);
            try { escrever(b.array()); } catch (Exception e) { log("uhid: " + e); }
        }

        private void escrever(byte[] a) throws ErrnoException, IOException {
            int f = 0;
            while (f < a.length) {
                int n = Os.write(fd, a, f, a.length - f);
                if (n <= 0) throw new IOException("write uhid");
                f += n;
            }
        }

        void fechar() {
            try {
                ByteBuffer b = ByteBuffer.allocate(4).order(ByteOrder.nativeOrder());
                b.putInt(UHID_DESTROY);
                escrever(b.array());
            } catch (Exception ignored) { }
            try { Os.close(fd); } catch (Exception ignored) { }
        }
    }

    static void log(String s) { System.out.println(s); System.out.flush(); }

    // ------------------------------------------------------------------ injecao de tecla (acordar)
    private static Object inputManager;
    private static Method injectMethod;

    private static void prepararInjecao() {
        try {
            Class<?> c;
            try {
                c = Class.forName("android.hardware.input.InputManagerGlobal");
            } catch (ClassNotFoundException e) {
                c = Class.forName("android.hardware.input.InputManager");
            }
            inputManager = c.getDeclaredMethod("getInstance").invoke(null);
            injectMethod = c.getMethod("injectInputEvent", android.view.InputEvent.class, int.class);
        } catch (Exception e) {
            log("injecao indisponivel: " + e);
        }
    }

    // id do nosso mouse no Android: a posicao lida no dumpsys tem de ser a DELE (o scrcpy tambem
    // deixa um "mouse" injetado la, que ja estragou uma calibracao)
    private static volatile int idMouse = -999;

    /** Espera o Android registrar o aparelho (senao os primeiros movimentos se perdem). */
    private static void esperarAparelho(String nome) {
        long fim = SystemClock.uptimeMillis() + 1500;
        try {
            Method ids = inputManager.getClass().getMethod("getInputDeviceIds");
            Method dev = inputManager.getClass().getMethod("getInputDevice", int.class);
            while (SystemClock.uptimeMillis() < fim) {
                for (int id : (int[]) ids.invoke(inputManager)) {
                    InputDevice d = (InputDevice) dev.invoke(inputManager, id);
                    if (d != null && nome.equals(d.getName())) { idMouse = id; SystemClock.sleep(40); return; }
                }
                SystemClock.sleep(10);
            }
            log("mouse nao apareceu a tempo");
        } catch (Exception e) {
            SystemClock.sleep(300);
        }
    }

    private static Uhid criarMouse() throws ErrnoException, IOException {
        long t0 = SystemClock.uptimeMillis();
        Uhid m = new Uhid("Italophone mouse", 0x7001, DESC_MOUSE);
        esperarAparelho("Italophone mouse");
        if (DEPURAR) log("mouse pronto em " + (SystemClock.uptimeMillis() - t0) + " ms");
        return m;
    }

    // ------------------------------------------------------------------ calibracao
    // O Android 16 multiplica todo movimento do mouse (~2x num celular de 400 dpi) por conta propria; o 11 nao.
    // Mede-se o fator ao conectar e divide-se cada movimento por ele: o notebook manda pixels exatos.
    private static final boolean DEPURAR = false;
    private static double ganho = 1.0;
    private static double restoX, restoY;
    // ao recriar o mouse a setinha nasce no meio da tela (Android novo) ou fica onde estava (antigo)?
    // 'C' nasce no meio, 'K' fica onde estava, 'U' nao deu para saber
    private static char nasce = 'U';

    private static double[] posicaoSetinha() {
        try {
            Process p = Runtime.getRuntime().exec(new String[] { "dumpsys", "input" });
            java.io.BufferedReader r = new java.io.BufferedReader(new java.io.InputStreamReader(p.getInputStream()));
            java.util.regex.Pattern pat = java.util.regex.Pattern.compile("mDeviceStates=" + idMouse + ":\\[.*?hoveringPointers=\\[Pointer\\(id=0, MOUSE\\) at \\(([-\\d.]+), ([-\\d.]+)\\)");
            String l; double[] achou = null;
            while ((l = r.readLine()) != null) {
                if (achou != null) continue;
                java.util.regex.Matcher m = pat.matcher(l);
                if (m.find()) achou = new double[] { Double.parseDouble(m.group(1)), Double.parseDouble(m.group(2)) };
            }
            p.waitFor();
            return achou;
        } catch (Exception e) {
            return null;
        }
    }

    private static void moverCru(Uhid m, int dx, int dy) {
        m.enviar(new byte[] { 0, (byte) dx, (byte) (dx >> 8), (byte) dy, (byte) (dy >> 8), 0, 0 }, 7);
    }

    private static void calibrar() {
        Uhid m = null;
        try {
            m = criarMouse();
            moverCru(m, -32767, -32767);
            SystemClock.sleep(650);
            moverCru(m, 100, 100);
            SystemClock.sleep(150);
            double[] a = posicaoSetinha();
            if (a != null && a[0] > 20 && a[0] < 1000) ganho = a[0] / 100.0;
            m.fechar(); m = null;
            SystemClock.sleep(150);
            m = criarMouse();
            moverCru(m, 1, 0);
            SystemClock.sleep(150);
            double[] b = posicaoSetinha();
            if (a != null && b != null) {
                nasce = Math.abs(b[1] - a[1]) > 5 || Math.abs(b[0] - a[0]) > ganho * 3 + 2 ? 'C' : 'K';
                calibrado = true;
                gravarCache();
                log("ganho do Android " + ganho + ", setinha ao recriar: " + nasce);
            } else {
                log("calibracao sem resposta (tela apagada?)");
            }
        } catch (Exception e) {
            log("calibracao falhou: " + e);
        } finally {
            if (m != null) m.fechar();
        }
    }

    // A medida so funciona com a tela acesa; a ultima boa fica guardada no celular.
    private static final String CACHE = "/data/local/tmp/italophone-calibracao";
    private static boolean calibrado;

    private static void gravarCache() {
        try (java.io.FileWriter w = new java.io.FileWriter(CACHE)) {
            w.write(ganho + " " + nasce);
        } catch (Exception e) { log("cache: " + e); }
    }

    private static void lerCache() {
        try (java.io.BufferedReader r = new java.io.BufferedReader(new java.io.FileReader(CACHE))) {
            String[] p = r.readLine().trim().split(" ");
            ganho = Double.parseDouble(p[0]);
            nasce = p[1].charAt(0);
            calibrado = true;
            log("calibracao guardada: ganho " + ganho + ", setinha ao recriar: " + nasce);
        } catch (Exception ignored) { }
    }

    /** Ao conectar: mede (Android 14+, onde o sistema escala o mouse) ou usa a medida guardada. */
    private static void prepararCalibracao() {
        if (android.os.Build.VERSION.SDK_INT < 34) { calibrado = true; return; }   // Android antigo: 1:1
        calibrar();
        if (!calibrado) lerCache();
    }

    /** Converte o movimento pedido (pixels exatos) para o que se manda ao Android. */
    private static void ajustar(byte[] rel) {
        if (ganho == 1.0) return;
        int dx = (short) ((rel[1] & 0xFF) | (rel[2] << 8)), dy = (short) ((rel[3] & 0xFF) | (rel[4] << 8));
        double fx = dx / ganho + restoX, fy = dy / ganho + restoY;
        int ix = (int) Math.round(fx), iy = (int) Math.round(fy);
        // empurroes contra a borda nao guardam resto
        restoX = Math.abs(dx) > 30000 ? 0 : fx - ix;
        restoY = Math.abs(dy) > 30000 ? 0 : fy - iy;
        rel[1] = (byte) ix; rel[2] = (byte) (ix >> 8);
        rel[3] = (byte) iy; rel[4] = (byte) (iy >> 8);
        if (DEPURAR) log("M " + dx + "," + dy + " -> " + ix + "," + iy);
    }

    private static void tecla(int keycode) {
        if (injectMethod == null) return;
        long t = SystemClock.uptimeMillis();
        for (int acao : new int[] { KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP }) {
            KeyEvent e = new KeyEvent(t, t, acao, keycode, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD);
            try { injectMethod.invoke(inputManager, e, 0); } catch (Exception ex) { log("inject: " + ex); }
        }
    }

    // ------------------------------------------------------------------ principal
    public static void main(String[] args) throws Exception {
        prepararInjecao();
        LocalServerSocket ss = new LocalServerSocket("italophone");
        log("pronto");
        while (true) {
            LocalSocket s = ss.accept();
            log("notebook conectou");
            atender(s);
            log("notebook saiu");
        }
    }

    private static void atender(LocalSocket s) {
        Uhid mouse = null, teclado = null, midia = null;
        try {
            teclado = new Uhid("Italophone teclado", 0x7002, DESC_TECLADO);
            midia = new Uhid("Italophone midia", 0x7003, DESC_MIDIA);
            prepararCalibracao();
            // mouse ja pronto (invisivel ate o primeiro movimento): a entrada nao espera criar
            mouse = criarMouse();
            // 'S' = servidor novo: movimento ja corrigido; depois 'C', 'K' ou 'U' (onde a setinha nasce).
            // Se a medida so vier depois (na primeira entrada), manda de novo so a letra.
            java.io.OutputStream out = s.getOutputStream();
            out.write(new byte[] { 'S', (byte) nasce });
            DataInputStream in = new DataInputStream(s.getInputStream());
            byte[] rel = new byte[7];
            byte[] kb = new byte[8];
            byte[] md = new byte[2];
            while (true) {
                int tipo = in.read();
                if (tipo < 0) break;
                switch (tipo) {
                    case 'M':
                        in.readFully(rel);   // ja vem no formato do relatorio: botoes, x(i16 LE), y(i16 LE), roda, rodaH
                        if (mouse == null) mouse = criarMouse();
                        ajustar(rel);
                        mouse.enviar(rel, 7);
                        break;
                    case 'K':
                        in.readFully(kb);
                        teclado.enviar(kb, 8);
                        break;
                    case 'C':
                        in.readFully(md);
                        midia.enviar(md, 2);
                        break;
                    case 'W':
                        if (DEPURAR) log("W");
                        if (!calibrado) {
                            // nunca medido e a tela estava apagada: acende e mede agora (uma vez so;
                            // a medida fica guardada)
                            tecla(KeyEvent.KEYCODE_WAKEUP);
                            SystemClock.sleep(400);
                            if (mouse != null) { mouse.fechar(); mouse = null; }
                            calibrar();
                            calibrado = true;   // mesmo sem resposta, nao tenta de novo nesta conexao
                            out.write(nasce);
                        }
                        if (mouse == null) mouse = criarMouse();
                        tecla(KeyEvent.KEYCODE_WAKEUP);
                        break;
                    case 'P':
                        if (DEPURAR) log("P");
                        // sem mouse ligado o Android tira a setinha da tela na hora
                        if (mouse != null) { mouse.fechar(); mouse = null; }
                        restoX = restoY = 0;
                        // ja deixa o proximo pronto (invisivel ate se mexer)
                        mouse = criarMouse();
                        break;
                    case 'Q':
                        return;
                    default:
                        log("mensagem desconhecida " + tipo);
                        return;
                }
            }
        } catch (Exception e) {
            log("erro: " + e);
        } finally {
            if (mouse != null) mouse.fechar();
            if (teclado != null) teclado.fechar();
            if (midia != null) midia.fechar();
            try { s.close(); } catch (IOException ignored) { }
        }
    }
}
