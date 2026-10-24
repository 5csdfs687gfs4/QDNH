# Guardian de salud y arranque del host

`Start-QdnhHost.ps1` vigila una sonda local que publica el propio QDNH; no considera suficiente que exista el proceso. No abre puertos de diagnóstico ni transmite RF. El supervisor fija el directorio de trabajo a `state/` y lanza `--headless`, sin prompts ni lecturas repetidas de stdin. El modo normal también sale limpiamente ante EOF.

## Qué se comprueba

La sonda `QDNH_HEARTBEAT_FILE` es opcional fuera del supervisor. Contiene protocolo 1, PID, fecha exacta de inicio, secuencia creciente, estado y datos de recursos. Un heartbeat antiguo o de otro proceso no cuenta como progreso. La publicación es atómica y no incluye contraseñas. El guardian mide los plazos con reloj monotónico; no depende de que la hora UTC permanezca fija.

Antes de publicar, la sonda atraviesa el cerrojo real de la autoridad de sesiones y consulta estado del puerto. Si esa ruta queda bloqueada, la secuencia se congela aunque el proceso continúe vivo. Distingue Initializing, Running y Degraded. Además comprueba recursos configurados:

- Puerto serie abierto y ciclos de lectura con timeout progresando (SerialPort). No se interpreta silencio de un tty bloqueante como fallo.
- Captura inicializada y callbacks de audio recientes, con tolerancia inicial de 15 segundos.
- Reproductor WASAPI en estado Playing o tarea ALSA sin finalizar.

Estas señales comprueban progreso interno y recursos; no demuestran recepción RF, calidad del audio, ni estado físico del PTT. Hay que validar la cadencia de captura y el comportamiento de los controladores AIOC reales antes de activar el reinicio por Degraded en uso diario.

## Plazos y recuperación

Valores por defecto configurables al invocar el supervisor:

| Parámetro | Predeterminado |
|---|---:|
| StartupGraceSeconds | 90 |
| HeartbeatTimeoutSeconds | 15 |
| DegradedTimeoutSeconds | 120; 0 desactiva reinicio por recurso degradado |
| GracefulStopSeconds | 15 |
| MaximumRestarts | 5 fallos consecutivos |
| BackoffBaseSeconds | 2; aumenta hasta 30 |

Si no se completa el arranque, no avanza la sonda o un recurso esperado permanece degradado, el guardian solicita parada mediante archivo local y Q. Si la instancia bloqueada no responde dentro del plazo, termina exclusivamente el proceso hijo cuya identidad PID/fecha sigue coincidiendo y lo relanza. Una instancia estable durante diez minutos reinicia el contador de fallos. El cerrojo se mantiene durante la recuperación: no se abren supervisores o instancias nuevas en paralelo.

Al abrir UART, tanto tras arranque como recuperación USB, se manda ReleaseKey antes de admitir comandos. Esto reduce el riesgo de conservar PTT tras un proceso muerto, pero no garantiza que la radio suelte físicamente el PTT durante el intervalo de reinicio. Verificarlo con radio y dos clientes; una protección independiente en firmware/equipo es aconsejable para emisiones.

La parada manual con Stop-QdnhHost.ps1 conserva una conducta distinta: pide salida y no vuelve a arrancar; si el proceso no responde mantiene la espera para revisión. No mata otros QDNH encontrados por nombre ni instalaciones fuera de HostRoot.

## Después de reiniciar Windows

Register-QdnhLogonTask.ps1 conserva Logon como opción predeterminada y no arranca al registrar. Tres opciones explícitas de StartupMode:

- Logon: usuario actual, sesión interactiva, privilegios normales. Mantiene el contexto donde se ha validado WASAPI. Para funcionar después del reinicio alguien debe iniciar sesión; no se configura inicio automático de usuario.
- StartupSerialOnly: tarea SYSTEM al arrancar Windows, retraso de 30 segundos, solo si Mode=serial. No presupone audio.
- StartupAudioExperimental: tarea SYSTEM al arrancar, con autorización explícita para probar audio en Session 0. No afirma que WASAPI/AIOC funcionen allí; esa prueba decide si es utilizable o hay que conservar Logon.

Las tareas tienen exclusión de instancias y reintento acotado del supervisor. Las opciones SYSTEM requieren permisos de administración del equipo. Los secretos de cuentas configurados solo en el entorno del usuario no están automáticamente disponibles para SYSTEM: preparar su acceso autorizado o el servidor fallará cerrado. No se copian secretos a tareas ni se guardan contraseñas de inicio de sesión.

## Vuelta de corriente y pruebas pendientes

El software no puede encender un ordenador sin alimentación. La opción del firmware tipo Restore on AC Power Loss/Power On after AC Loss tiene que verificarse en el NucBox concreto y probarse desconectando/reponiendo corriente en una ventana prevista. No se ha modificado BIOS, autologon, credenciales, tareas ni configuración del NucBox.

La elección Logon/StartupAudioExperimental y el encendido al volver corriente quedan como validación del equipo real. Para instalar una tarea, detener primero cualquier servidor externo a HostRoot y guardar su versión/configuración.

## Evidencia automática

La prueba HostHeartbeatTests bloquea deliberadamente el probe de autoridad y comprueba que deja de publicar secuencias aunque el worker exista. Test-HostScripts.ps1 con SupervisorFixture usa un ejecutable sin radio/audio: primera instancia sigue viva pero congela el heartbeat e ignora parada; se detecta, termina y aparece una segunda instancia con sonda creciente. Luego se solicita salida limpia y se comprueba que no quedan procesos. No registra tareas ni modifica firmware.

Los paquetes anteriores a este heartbeat no pueden usarse con la vigilancia de salud nueva: restaurarlos requiere un supervisor compatible o lanzamiento manual de la versión anterior. No presentar ausencia de sonda como salud correcta.


La salud incluye los listeners TCP esperados por el modo configurado (audio, serie y control compartido). Un error al ocupar el puerto o un listener cerrado produce Degraded, aunque los dispositivos esten abiertos y el proceso exista. Una prueba ocupa realmente un puerto y verifica que esa apertura fallida no se considera lista; tambien verifica perdida de disponibilidad al cerrar el listener. No se han cambiado direcciones bind ni se han abierto puertos adicionales en el NucBox.
