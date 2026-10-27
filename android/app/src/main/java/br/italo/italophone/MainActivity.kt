package br.italo.italophone

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast

// Abrir o app pelo icone so confere a permissao e liga a setinha; quem manda e o notebook.
class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (!Settings.canDrawOverlays(this)) {
            Toast.makeText(this, "Permita o Italophone sobrepor outros apps", Toast.LENGTH_LONG).show()
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        } else {
            startForegroundService(Intent(this, Borda::class.java))
            Toast.makeText(this, "Italophone ligado. Use o mouse do notebook.", Toast.LENGTH_SHORT).show()
        }
        finish()
    }
}
