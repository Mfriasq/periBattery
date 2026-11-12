# PeriBattery

Muestra la batería de tus periféricos inalámbricos en una píldora sobre la barra de tareas de Windows 11. Gratis, sin anuncios, sin telemetría y sin conexiones de red.

Lee la batería **directamente de los dispositivos**, sin usar Logitech G HUB, y puede funcionar a la vez que G HUB.

## Dispositivos compatibles

| Dispositivo | Cómo se lee |
|---|---|
| Teclados y ratones Logitech con receptor LIGHTSPEED (probado: G515 TKL, G703 HERO) | HID++ 2.0, características 0x1004, 0x1001 y 0x1000 |
| Auriculares Logitech G522 (por dongle o por cable USB) | Protocolo Centurion, característica 0x0104 |
| Mando Xbox Wireless y otros dispositivos Bluetooth LE | Nivel de batería que guarda Windows |

## Instalación

- **Instalador**: ejecuta `PeriBattery-Setup-x.y.z.exe`. Se instala solo para tu usuario, sin permisos de administrador. Puedes marcar "Iniciar PeriBattery con Windows".
- **Portable**: descomprime el zip y ejecuta `PeriBattery.exe`. No necesita instalar .NET.

Los ajustes se guardan en `%AppData%\PeriBattery\settings.json` y el registro en `%AppData%\PeriBattery\logs\peribattery.log`.

## Uso

- La píldora aparece a la izquierda de los iconos de la bandeja. Verde: más del 50 %. Naranja: entre 10 y 50 %. Rojo: menos del 10 %. Un rayo indica que está cargando.
- Un dispositivo atenuado está en reposo o apagado; se muestra su último nivel conocido.
- Clic en la píldora o en el icono de la bandeja: abre el panel con las pestañas **Dispositivos**, **General** y **Diseño**.
- En **Dispositivos** puedes cambiar el nombre (clic sobre el nombre) y decidir con el interruptor si cada uno aparece en la píldora.
- Clic derecho en la píldora o en el icono: menú con "Actualizar ahora" y "Salir".
- La batería se actualiza cada 60 segundos y al conectar o desconectar un dispositivo.
- Con batería al 10 % o menos recibes una notificación, una sola vez por ciclo de descarga.
- La píldora se oculta mientras haya una aplicación a pantalla completa (exclusiva o sin bordes) en el monitor de la barra de tareas.
- Con varios monitores aparece una píldora en la barra de tareas de cada pantalla, y cada una se oculta según lo que haya en su monitor. Se desactiva en **Diseño → Mostrar en todos los monitores**.
- Un ratón o teclado conectado por cable para cargar se lee por USB. Mientras carga, los dispositivos que informan por voltaje (como el G703) muestran un porcentaje más alto que el real, porque el voltaje sube al cargar.

## Compilar

Requisitos: [.NET 8 SDK](https://dot.net) y, para el instalador, [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
# Todo: tests, exe autocontenido, zip portable e instalador (salida en dist\)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0

# Solo compilar y probar
dotnet build PeriBattery.sln -c Release
dotnet test tests\PeriBattery.Tests -c Release

# Solo el exe portable
dotnet publish src\PeriBattery -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

## Herramienta de diagnóstico

`PeriBattery.HidDiag.exe` (incluida en el zip portable) solo hace lecturas:

```powershell
PeriBattery.HidDiag.exe list --logitech   # colecciones HID: VID, PID, página de uso, tamaños e IDs de informe
PeriBattery.HidDiag.exe hidpp --raw       # sondea los receptores HID++ y vuelca cada trama
PeriBattery.HidDiag.exe centurion         # sondea auriculares Centurion (G522) con volcado en crudo
PeriBattery.HidDiag.exe watch --seconds 120 --raw   # el mismo motor que la app, con estadísticas al final
```

## Solución de problemas

**No aparece ningún dispositivo.** Ejecuta `PeriBattery.HidDiag.exe list --logitech` y comprueba que salen colecciones con página de uso `0xFF00` (receptores) o `0xFFA0` (auriculares). Si no salen, Windows no ve el receptor: cámbialo de puerto USB.

**Un dispositivo aparece atenuado ("En reposo").** Está dormido o apagado. Se actualiza al volver a usarlo o en el siguiente sondeo.

**Conflictos con G HUB.** PeriBattery abre los receptores en modo compartido y marca cada petición con su propio identificador de software (`0x0E`), así que no consume las respuestas de G HUB ni escribe ninguna configuración. Si aun así ves problemas:

1. Mira la pestaña **General → Comunicación con los dispositivos**. `timeouts` o `busy` que crecen sin parar indican interferencia. El panel marca el dispositivo como "lectura poco fiable" tras tres fallos seguidos.
2. Activa **Registro de depuración**, espera un par de minutos y revisa el registro: verás cada trama enviada (`TX`) y recibida (`RX`).
3. Si otro programa usa el mismo identificador de software, cambia `HidppSoftwareId` en `settings.json` (valor de 1 a 15; evita 1, 7, 10, 11, 13 y 15) y reinicia la app.
4. Si aparece el aviso "cannot open ... another program may hold it exclusively" en el registro, algún programa ha abierto el receptor en exclusiva. Ciérralo (no suele ser G HUB) y pulsa "Actualizar ahora".
5. Si G HUB se comporta de forma rara mientras actualiza el firmware de un dispositivo, sal de PeriBattery durante la actualización.

PeriBattery nunca recurre a G HUB para obtener datos: si no puede leer un dispositivo, lo indica y conserva el último nivel.

**El G522 aparece "En reposo" con el dongle conectado.** Los auriculares están apagados, o conectados por cable: en ese caso se leen por USB y el estado indica "USB · cargando".

**El mando Xbox no cambia a "Conectado".** El nivel y el estado salen de Windows (Configuración → Bluetooth y dispositivos). Si Windows tampoco lo muestra conectado, vuelve a emparejarlo.

**No llegan las notificaciones.** Ejecuta `PeriBattery.exe --test-toast`. Si no aparece nada, revisa Configuración → Sistema → Notificaciones: que estén activadas, que PeriBattery esté permitido y que "No molestar" esté apagado.

**La píldora no se ve.** Comprueba que algún dispositivo tenga el interruptor activado en **Dispositivos** y que no haya una ventana a pantalla completa en ese monitor. Si reiniciaste el Explorador, la píldora se recoloca sola en unos segundos.

## Desinstalar

Desde Configuración → Aplicaciones, o con el desinstalador del menú Inicio. Se eliminan el programa, el inicio automático, el registro de notificaciones y la carpeta `%AppData%\PeriBattery`. En modo portable: desactiva "Iniciar con Windows", sal de la app y borra el exe y esa carpeta.
