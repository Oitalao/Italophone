package br.italo.italophone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.ServiceInfo
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.util.Base64
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.concurrent.thread

// Uma faixa invisivel de 3 px na borda do celular que fica do lado do notebook.
// Quando a setinha do mouse passa por cima dela, o Android manda eventos de "hover"
// com a posicao exata; o notebook usa isso para devolver o mouse ao Windows.
//
// O notebook conecta por adb forward na porta 27440 e manda:
//   "B lado"   mostra a faixa (0 esquerda, 1 direita, 2 cima, 3 baixo); "B -1" esconde
//   "T texto"  area de transferencia do notebook (texto em base64), para por no celular
// O celular manda:
//   "S largura altura"        ao conectar e quando gira
//   "H dentro posicao"        setinha na faixa (dentro 1/0), posicao em px ao longo da borda
//   "C texto"                 area de transferencia do celular mudou (texto em base64)
//
// A partir do Android 10 so o teclado padrao (ou quem tem o foco da tela) le a area de
// transferencia em segundo plano. Por isso, ao conectar, este servico vira o teclado padrao por
// conta propria (ClipboardIme, um teclado vazio) usando WRITE_SECURE_SETTINGS, que o notebook
// concede por adb ao preparar o app; e volta ao teclado de antes (Gboard ou outro) assim que a
// conexao cai, inclusive por tirar o cabo: o adbd do celular derruba o socket na hora, sem
// precisar de nenhum aviso vindo do notebook. Por no celular nao e restrito (so a leitura e),
// por isso aquele lado sempre funcionou.
class Borda : Service() {
    private val porta = 27440
    private val espessura = 3
    private val main = Handler(Looper.getMainLooper())
    private lateinit var wm: WindowManager
    private lateinit var cm: ClipboardManager
    private var faixa: View? = null
    private var lado = -1
    @Volatile private var saida: OutputStream? = null
    @Volatile private var cliente: Socket? = null
    private var servidor: ServerSocket? = null
    @Volatile private var vivo = true
    @Volatile private var ultimoRecebidoDoNotebook: String? = null
    private val IME_ID = "br.italo.italophone/.ClipboardIme"
    private val prefs by lazy { getSharedPreferences("italophone", MODE_PRIVATE) }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        wm = getSystemService(WindowManager::class.java)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("borda", "Mouse do notebook", NotificationManager.IMPORTANCE_MIN))
        val n = Notification.Builder(this, "borda")
            .setSmallIcon(R.drawable.icone)
            .setContentTitle("Italophone")
            .setContentText("Mouse e teclado do notebook")
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(1, n)
        cm = getSystemService(ClipboardManager::class.java)
        // se o servico morreu sem passar pelo onDestroy (forcado, bateria etc.) com o teclado
        // ainda trocado, desfaz agora, antes de aceitar qualquer conexao nova
        restaurarTeclado()
        cm.addPrimaryClipChangedListener {
            try {
                val item = cm.primaryClip?.let { c -> if (c.itemCount > 0) c.getItemAt(0) else null } ?: return@addPrimaryClipChangedListener
                val texto = item.coerceToText(this)?.toString() ?: return@addPrimaryClipChangedListener
                if (texto.isEmpty() || texto == ultimoRecebidoDoNotebook) return@addPrimaryClipChangedListener
                enviar("C " + Base64.encodeToString(texto.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
            } catch (_: Exception) {}
        }
        thread(name = "servidor") { servir() }
        thread(name = "remetente", isDaemon = true) { remeter() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        vivo = false
        try { servidor?.close() } catch (_: Exception) {}
        try { cliente?.close() } catch (_: Exception) {}
        main.post { mostrar(-1); restaurarTeclado() }
        super.onDestroy()
    }

    // Vira o teclado padrao por conta propria (ver o comentario no topo do arquivo). Guarda qual
    // era o teclado de antes para devolver; se ja somos o teclado padrao (reconexao), nao faz nada.
    // So le/escreve DEFAULT_INPUT_METHOD: a partir do Android 14, ENABLED_INPUT_METHODS so pode ser
    // lida por quem tem targetSdk <= 33 (vaza quais apps estao instalados); o notebook deixa o
    // nosso teclado habilitado por adb (ime enable) antes de qualquer conexao, uma vez so.
    private fun ativarTeclado() {
        try {
            val cr = contentResolver
            val atual = Settings.Secure.getString(cr, Settings.Secure.DEFAULT_INPUT_METHOD) ?: return
            if (atual == IME_ID) return
            prefs.edit().putString("teclado_anterior", atual).apply()
            Settings.Secure.putString(cr, Settings.Secure.DEFAULT_INPUT_METHOD, IME_ID)
        } catch (_: Exception) {}
    }

    // Devolve o teclado de antes.
    private fun restaurarTeclado() {
        try {
            val anterior = prefs.getString("teclado_anterior", null) ?: return
            Settings.Secure.putString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD, anterior)
            prefs.edit().remove("teclado_anterior").apply()
        } catch (_: Exception) {}
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        main.postDelayed({ mandarTamanho(); if (lado >= 0) { val l = lado; mostrar(-1); mostrar(l) } }, 300)
    }

    private fun tamanho(): Pair<Int, Int> {
        if (Build.VERSION.SDK_INT >= 30) {
            val b = wm.maximumWindowMetrics.bounds
            return Pair(b.width(), b.height())
        }
        @Suppress("DEPRECATION")
        val p = android.graphics.Point().also { wm.defaultDisplay.getRealSize(it) }
        return Pair(p.x, p.y)
    }

    // um remetente so, em ordem: "H 1" e "H 0" seguidos nunca chegam trocados ao notebook
    private val fila = java.util.concurrent.LinkedBlockingQueue<String>()

    private fun enviar(s: String) {
        if (saida == null) return
        fila.offer(s)
    }

    private fun remeter() {
        while (vivo) {
            val s = try { fila.take() } catch (_: InterruptedException) { break }
            val o = saida ?: continue
            try { o.write((s + "\n").toByteArray()); o.flush() } catch (_: Exception) {}
        }
    }

    private fun mandarTamanho() {
        val (w, h) = tamanho()
        enviar("S $w $h")
    }

    private fun servir() {
        while (vivo) {
            try {
                val ss = ServerSocket(porta, 2, InetAddress.getByName("127.0.0.1"))
                servidor = ss
                while (vivo) {
                    val c = ss.accept()
                    c.tcpNoDelay = true
                    try { cliente?.close() } catch (_: Exception) {}
                    cliente = c
                    fila.clear()
                    saida = c.getOutputStream()
                    main.post { mandarTamanho(); ativarTeclado() }
                    thread(name = "cliente") { atender(c) }
                }
            } catch (_: Exception) {
                if (vivo) Thread.sleep(1000)
            }
        }
    }

    private fun atender(c: Socket) {
        try {
            val r = BufferedReader(InputStreamReader(c.getInputStream()))
            while (vivo) {
                val l = r.readLine() ?: break
                val p = l.trim().split(' ')
                if (p[0] == "B" && p.size == 2) {
                    val novo = p[1].toIntOrNull() ?: -1
                    main.post { mostrar(novo) }
                } else if (p[0] == "T" && p.size == 2) {
                    try {
                        val texto = String(Base64.decode(p[1], Base64.NO_WRAP), Charsets.UTF_8)
                        ultimoRecebidoDoNotebook = texto
                        cm.setPrimaryClip(ClipData.newPlainText("Italophone", texto))
                    } catch (_: Exception) {}
                }
            }
        } catch (_: Exception) {
        } finally {
            try { c.close() } catch (_: Exception) {}
            if (cliente === c) main.post { mostrar(-1); restaurarTeclado() }
        }
    }

    private fun mostrar(novo: Int) {
        faixa?.let { try { wm.removeView(it) } catch (_: Exception) {} }
        faixa = null
        lado = novo
        if (novo !in 0..3) return
        val (w, h) = tamanho()
        val vertical = novo == 0 || novo == 1
        val lp = WindowManager.LayoutParams(
            if (vertical) espessura else w,
            if (vertical) h else espessura,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = when (novo) {
                0 -> Gravity.LEFT or Gravity.TOP
                1 -> Gravity.RIGHT or Gravity.TOP
                2 -> Gravity.LEFT or Gravity.TOP
                else -> Gravity.LEFT or Gravity.BOTTOM
            }
            x = 0; y = 0
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
            if (Build.VERSION.SDK_INT >= 30) setFitInsetsTypes(0)
            title = "Italophone borda"
        }
        val v = View(this)
        v.setOnHoverListener { _, e ->
            val pos = if (vertical) e.rawY else e.rawX
            when (e.actionMasked) {
                MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE -> enviar("H 1 ${pos.toInt()}")
                MotionEvent.ACTION_HOVER_EXIT -> enviar("H 0 ${pos.toInt()}")
            }
            true
        }
        // a faixa so existe enquanto o mouse do notebook esta no celular. Sem a permissao de
        // sobrepor (alguns aparelhos negam), segue sem ela: o notebook devolve o mouse pela estimativa.
        try {
            wm.addView(v, lp)
            faixa = v
        } catch (_: Exception) {
        }
    }
}
