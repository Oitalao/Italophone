package br.italo.italophone

import android.inputmethodservice.InputMethodService
import android.view.Gravity
import android.view.View
import android.widget.TextView

// Teclado vazio: existe so para o Android aceitar este app como "o teclado padrao", o que libera
// a leitura da area de transferencia em segundo plano (Borda.kt troca para ele ao conectar e
// devolve o Gboard, ou o que estava antes, ao desconectar). Enquanto ligado, digitar na tela do
// celular nao funciona (o teclado de verdade volta sozinho ao tirar o cabo).
class ClipboardIme : InputMethodService() {
    override fun onCreateInputView(): View {
        val tv = TextView(this)
        tv.text = "Italophone ligado — desconecte o cabo para digitar no celular"
        tv.gravity = Gravity.CENTER
        tv.setPadding(24, 40, 24, 40)
        tv.setBackgroundColor(0xFF1B1B1B.toInt())
        tv.setTextColor(0xFFFFFFFF.toInt())
        return tv
    }
}
