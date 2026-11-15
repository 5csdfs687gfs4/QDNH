# MultiRX: segundo receptor por OpenWebRX

MultiRX añade a QDNH un receptor opcional que no es la radio local: QDNH se conecta como un cliente web más a un OpenWebRX remoto, lo sintoniza y reenvía el audio a QDockX. En la app es el interruptor **MultiRX ON/OFF**; con ON suena el SDR en lugar del walkie.

Está apagado por defecto. Apagado, el puerto extra no se abre y el servidor se comporta igual que antes.

## Puesta en marcha (consola de QDNH)

| Comando | Qué hace |
|---|---|
| `WU http://host:8073` | Dirección del OpenWebRX (`WU none` la borra y apaga MultiRX) |
| `WF 145.500 nfm` | Frecuencia en MHz y modo (`nfm`, `am`, `usb`, `lsb`, `cw`) |
| `W on` / `W off` | Enciende o apaga MultiRX |
| `W` | Muestra configuración y estado |

Se guardan en el `.conf` como `OpenWebRxEnabled`, `OpenWebRxUrl`, `OpenWebRxFrequencyHz` y `OpenWebRxModulation`. Un QDNH anterior que lea ese fichero avisará de claves desconocidas y seguirá funcionando.

Hay que abrir en el router/cortafuegos el puerto **base+3** (TCP), igual que los otros.

## Cómo funciona

- **Audio**: puerto `NetworkPort+3`, solo de salida. Misma autenticación que los demás canales (contraseña o cuentas individuales). PCM mono 16 bits a 22050 Hz, como el resto del audio. OpenWebRX entrega 12000 Hz en ADPCM; QDNH lo decodifica y remuestrea.
- **Sintonía**: mensajes de control `SdrTune` (15), `SdrTuneDenied` (16) y `SdrStatus` (17), solo en Shared Radio Mode. Para resintonizar hace falta tener el control, como con cualquier comando a la radio. Motivos de rechazo: `SDR_DISABLED`, `CONTROL_REQUIRED`, `OUT_OF_RANGE`, `INVALID_FREQUENCY`, `INVALID_MODULATION`, `INVALID_FILTER`. En modo tradicional (un usuario) la frecuencia se fija con `WF`.
- **Capacidad anunciada**: `multirx-v1` en HelloAck cuando está encendido.
- **Uso del SDR**: QDNH solo se conecta al OpenWebRX mientras hay algún oyente en el puerto base+3 y suelta la plaza unos segundos después de que se vaya el último. Si el SDR rechaza la conexión, reintenta espaciando (hasta 30 s).
- **Perfil del SDR**: QDNH nunca cambia el perfil del OpenWebRX. Solo puede sintonizar dentro de lo que cubre el perfil activo (frecuencia central ± mitad del ancho); fuera de eso responde `OUT_OF_RANGE`.
- **Aislamiento**: MultiRX no toca audio, serie ni TX de la radio local, y no cuenta para la salud que vigila el guardián: un SDR caído no degrada ni reinicia el servidor.

## Limitaciones conocidas

- El nivel `SmeterRaw` es el valor relativo de OpenWebRX, no dBm.
- No hay WFM ni modos digitales (solo audio a 12 kHz).
- El audio del SDR llega con más retardo que el de la radio local.
- Implementación reconstruida a partir del protocolo de QDockX y de `tools/rfcapture/openwebrx_capture.py`. Probada con un OpenWebRX simulado (`QDNH.Tests/MultiRxTests.cs`); **pendiente de validar contra el OpenWebRX real y con QDockX**.
