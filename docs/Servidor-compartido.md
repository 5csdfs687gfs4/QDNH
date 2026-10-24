# Servidor compartido: contrato y validación

Este cambio conserva los canales TCP y el reto SHA256 existente. No configura puertos, contraseñas ni dispositivos del equipo donde se ejecuta.

## Compatibilidad

El framing de control conserva tipo byte + longitud int32 little-endian + JSON UTF-8. No se renumeran mensajes 1–14. Se reservan 15–17 para MultiRX del cliente, sin implementarlos aquí. Las extensiones usan:

| ID | Mensaje |
|---|---|
| 21 / 22 | HealthRequest / HealthState |
| 23 / 24 | OperatorsRequest / OperatorsState |
| 25 / 26 | QueueJoin / QueueLeave |

Hello admite ProtocolVersion (1 por defecto) y Capabilities opcionales. HelloAck añade ProtocolVersion=2, ServerVersion y Capabilities. Los clientes que anuncian versión 1 no reciben mensajes adicionales. Capacidades: health-v1, operators-v1, control-queue-v1, tx-monitor-v1. Los records de SharedRadio/ControlMessage.cs son la definición exacta del payload.

QueuePosition=0 significa fuera de la cola y 1 el primero. La cola es informativa: no concede control ni activa TX automáticamente. ControlRequest sigue denegándose mientras haya propietario. Una reconexión no puede enviar comandos mediante su conexión antigua ni liberar los permisos de la nueva.

SerialConnected informa de puerto abierto. AudioCaptureReady/AudioPlaybackReady informan de objetos de audio inicializados, no demuestran recepción RF ni que el controlador de audio siga saludable. FrequencyMHz/Modulation siguen siendo datos declarados por el cliente. No se añade sondeo CAT que mueva el estado de la radio.

## Transmisión y recuperación

En Shared Radio Mode, la pérdida del canal de control/audio/serie, pérdida USB, liberación de control, inactividad TX, límite máximo y cierre/reconfiguración del servidor ordenan KeyPress/ReleaseKey=19. El paquete usa exactamente el framing, CRC y XOR de QDockX Radio/Serial.SendCommand. El reensamblador CAT bloquea KeyPress/PTT=16 si no existe permiso TX, incluso cuando llega partido en TCP. Es una protección para el recorrido PTT del cliente actual; no constituye un intérprete de todas las operaciones de hardware que permitiría CAT.

UART separa lectura de escritura, serializa publicación/cierre y reintenta reapertura. Al recuperar USB envía ReleaseKey antes de aceptar nuevos comandos. El cierre cancela los reintentos y no espera indefinidamente a un controlador bloqueado. Un error del consumidor de datos no fuerza una reapertura del dispositivo.

Parámetros adicionales de configuración (dos líneas: nombre y valor), guardados por Save:

- TxLeaseSeconds: 10 por defecto, limitado a 2–120. Solo audio TX autorizado renueva este plazo.
- MaxTxSeconds: 180 por defecto, limitado a 5–1800. Audio continuo o RequestTx repetido no reinician este límite.

Audio RX se suprime hacia el emisor y los oyentes que admiten monitor durante TX. El monitor excluye siempre al emisor. Los canales control/audio independientes no permiten alinear exactamente los avisos Start/Stop con una muestra PCM: el cambio de fuente sigue siendo aproximado; no se anuncia framing PCM nuevo.

Las colas de salida son acotadas. Audio descarta bloques antiguos. Serie/control desconectan al cliente que no consume, sin descartar bytes CAT en silencio. Las escrituras de red vencen a los 3 segundos; autenticación y Hello/identificador inicial tienen plazo de 10 segundos. Cada listener admite hasta 64 conexiones simultáneas. Close cierra también las conexiones aceptadas.

## Evidencia y pendientes

`dotnet test QDNH.sln` cubre pérdida de permisos/PTT, inactividad y máximo continuo, reconexiones antiguas, CAT fragmentado, monitor sin eco/mezcla RX, negociación antigua/nueva, cola informativa, CRC/XOR, pérdida/reapertura USB con dobles y cierre durante reapertura. Una prueba TCP loopback verifica Hello fragmentado, respuestas concurrentes sin mezclar frames y cierre de sesión al detener el listener. No usa la radio ni transmite RF.

Antes de uso diario, verificar físicamente ReleaseKey, límite máximo, pérdida de red con PTT, pérdida/reconexión AIOC y alternancia de dos móviles. Escribir al puerto no confirma el estado real del PTT. Los comandos de hardware que evitan KeyPress necesitan revisión específica.

La autenticacion individual y roles son opcionales (Acceso-individual.md). Sin configurar cuentas se conserva el acceso compartido anterior. TLS y la activacion de una red privada siguen pendientes de puesta en servicio; no se han cambiado credenciales ni acceso existente.


Ampliacion posterior: ForwardTxAudio entrega al reproductor dentro de la misma autoridad de permisos, y Release limpia la cola de salida antes de otra concesion. Un error de limpieza bloquea TX con TX_OUTPUT_UNAVAILABLE hasta reinicializar. MultiListen recompone PCM16 antes del fan-out y las colas: descartar un bloque antiguo conserva muestras completas. La cancelacion efectiva de audio ya entregado al controlador necesita validacion fisica. Autenticacion individual y scripts de host estan descritos en Acceso-individual.md y Host-Windows-pruebas.md.


Correccion de compatibilidad de firmware: CMD_0801 de nicsure/quansheng-dock-fw revision431deaafac62fb2e9ff9800c87575070480fcb18 (app/uart.c744) interpreta Key &0x1f y trata el segundo byte como padding. El filtro PTT usa esa misma mascara:16,48,80 y variantes con padding siguen requiriendo permiso TX. Pruebas con paquetes CAT cifrados completos y fragmentados verifican denegacion antes/despues del permiso y aceptacion durante la concesion. No se ha emitido RF para estas pruebas.
