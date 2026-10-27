# Italophone

Use o mouse e o teclado do computador no seu celular Android, ouça o som do celular no computador e veja a tela dele numa janela. Tudo pelo cabo USB.

Leve o mouse para fora da borda da tela, do lado em que o celular está, e a setinha aparece no celular. O teclado passa a digitar nele. Leve a setinha de volta e ela volta ao computador.

## Baixar

**[Baixar o instalador para Windows](https://github.com/Oitalao/Italophone/releases/latest/download/Italophone-1.0.0-instalador.exe)**

O app do celular é instalado sozinho pelo cabo. Se quiser instalar à mão, ele também está na [página de downloads](https://github.com/Oitalao/Italophone/releases/latest) (`Italophone.apk`).

## Do que precisa

- Computador com Windows 10 ou 11.
- Celular com Android 11 ou mais novo.
- Um cabo USB que passe dados (alguns cabos só carregam).

## Primeira vez, passo a passo

1. **Instale o Italophone no computador.** Abra o instalador e vá clicando em "Avançar". Não pede senha de administrador. Ele fica perto do relógio do Windows.
2. **Ligue as "Opções do desenvolvedor" no celular.** Abra *Configurações > Sobre o telefone* e toque 7 vezes em *Número da versão* (em alguns celulares fica em *Informações do software*). Aparece a mensagem "Você é um desenvolvedor".
3. **Ligue a "Depuração USB".** Abra *Configurações > Sistema > Opções do desenvolvedor* e ligue *Depuração USB*.
   - Celular Xiaomi, Redmi ou Poco: ligue também *Depuração USB (configurações de segurança)*.
4. **Ligue o cabo.** Na tela do celular aparece "Permitir depuração USB?". Marque *Sempre permitir neste computador* e toque em *Permitir*.
5. Pronto. O ícone perto do relógio fica verde e a setinha já passa para o celular.

Se o celular pedir para escolher um modo do USB (transferir arquivos, fotos), não precisa escolher nada.

## Como usar

- **Mouse e teclado:** por padrão o celular fica à direita da tela mais à direita. Para mudar, clique duas vezes no ícone perto do relógio (*Organizar telas*): arraste o celular para o lado certo e ajuste a velocidade da setinha.
- **Som:** o som do celular toca no computador enquanto o cabo estiver ligado. Liga e desliga no menu do ícone.
- **Tela do celular:** no menu do ícone, *Mostrar a tela do celular*. Clicar na barra de cima da janela abre as notificações. Fechar a janela não fecha o Italophone.
- **Teclas de volume e música:** enquanto a setinha está no celular, elas vão para o celular.
- **Mais de um celular no cabo:** escolha qual usar em *Celular*, no menu do ícone.

## Problemas comuns

- **O ícone não fica verde.** Confira se a Depuração USB está ligada e se você tocou em *Permitir* no celular. Troque o cabo: muitos só carregam. Se o Windows não reconhecer o celular, instale o driver USB do fabricante (Samsung, Motorola, Xiaomi...).
- **Algum app reclama da depuração USB** (gov.br, bancos). Desligue a *Depuração USB* enquanto usa esse app e ligue de novo depois. Sem ela o Italophone não funciona.
- **Não sai som.** O som precisa de Android 11 ou mais novo, e alguns apps não deixam gravar o som deles.

## Como funciona

- No computador, o Italophone percebe o mouse empurrando a borda da tela e passa os movimentos pelo cabo.
- No celular, um pequeno programa cria um mouse e um teclado "de verdade" (pelo `/dev/uhid` do Android, com a permissão da depuração USB), então a setinha é a do próprio Android.
- O som e a tela vêm do [scrcpy](https://github.com/Genymobile/scrcpy), que vai junto no instalador.

## Para quem quer mexer no código

- `host/`: programa do Windows (C#, um arquivo só). Compila com `build.cmd`, usando o compilador do .NET Framework que já vem no Windows.
- `servidor/`: programa que roda no celular pela depuração USB (Java). Compila com `build.ps1` (Android SDK e JDK 17).
- `android/`: app do celular (Kotlin) que avisa quando a setinha chega na borda. Compila com `gradlew assembleRelease`.
- `instalador/` e `publicar.ps1`: geram o instalador com o [Inno Setup](https://jrsoftware.org/isinfo.php).

## Créditos

Feito por Ítalo Sêne, com a ajuda do Claude (Anthropic), do Codex (OpenAI) e do Antigravity (Google).

Licença MIT. O scrcpy que vai junto tem a licença dele (Apache 2.0).
