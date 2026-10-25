# Latencia RX y concesión TX por cliente — 25 octubre 2026

## Extensiones compatibles

El canal de control mantiene su protocolo y anuncia dos capacidades nuevas en HelloAck. Los clientes anteriores ignoran los campos añadidos y conservan la ruta tradicional. La APK nueva no manda informes de latencia a servidores que no anuncian la capacidad.

- `rx-latency-v1`: PlaybackReport (27) incluye RequestId, ClientTickMs, RequestedMs, QueueMs y Underruns; PlaybackTarget (28) responde únicamente a esa sesión. Cada SessionInfo conserva su propia política, limitada a 40–500 ms, con subidas de hasta 20 ms por informe y bajadas de 10 ms después de seis informes estables. QDockX informa como máximo cada cinco segundos, aplica la recomendación solo en modo automático y mantiene su adaptación local si necesita un margen mayor. El modo manual conserva su valor.
- `tx-request-id-v1`: RequestTx, TxGranted y TxDenied incluyen RequestId. TxReleased incluye RequestId y SessionId del turno que finalizó. El cliente rechaza respuestas antiguas sin cancelar un turno posterior. Los servidores antiguos sin esta capacidad no pueden distinguir las respuestas de dos pulsaciones consecutivas tan precisamente; conservan la protección anterior por intención y propiedad.

La recomendación RX no cambia Vars.LatencyMils ni la cola de reproducción TX de la radio. Las colas TCP de salida ya son independientes por conexión y acotadas; un móvil lento no aumenta el objetivo de los demás. QueueMs/Underruns son medidas del teléfono. Ida/vuelta mide el canal de control, no latencia acústica o RF. No hay sincronización de relojes entre dispositivos ni afirmación de latencia unidireccional medida.

## Comprobaciones

QDNH.Tests: 60 pruebas, incluidos tres clientes con objetivos independientes, recuperación gradual, límites, compatibilidad anterior, rechazo del canal sustituido y eco de identidad de turno. QDockX.Coordination.Tests: 24 comprobaciones con el coordinador, generador de audio y estado de concesión reales conectados a transportes simulados; cubren concesión tardía, denegada, cancelada, fallo de escritura, cambio de transporte, limpieza antigua frente a nuevo PTT, reserva excluyente sintonía/PTT y restauración que no pisa operaciones posteriores. QDockX.Multiuser.Tests: 38 comprobaciones.

No se han transmitido señales ni hecho medidas con varios teléfonos reales. Los resultados completados del emisor significan escritura local al transporte, no recepción del paquete por RF. La versión instalada en el NucBox no cambia al compilar estos fuentes; hace falta instalar el servidor actualizado y habilitar/verificar el modo compartido en una ventana sin operadores activos.

## Instalación compatible con el guardian

El paquete Windows x64 debe instalarse como una nueva release, conservando `C:\ProgramData\QDNH\state`, `current.json` y la tarea existente `QDNH-Servidor`. No contiene ni sustituye contraseñas, indicativos o selección de dispositivos.

1. Verificar en la estación que no hay transmisión ni operadores activos y guardar copia del estado. No concluir que no hay TX solo porque un cliente esté desconectado.
2. Solicitar parada con `tools\Stop-QdnhHost.ps1 -HostRoot C:\ProgramData\QDNH`. Esperar la salida limpia del guardian y su hijo. Si no terminan, revisar; no matar procesos por nombre ni iniciar otra instancia.
3. Con el host detenido, ejecutar el `scripts\host\Install-QdnhRelease.ps1` incluido con `-SourcePackage <carpeta-del-paquete> -HostRoot C:\ProgramData\QDNH -ReleaseName 20261025-multiusuario`. El instalador guarda respaldo y conserva la configuración. No suministrar InitialConfig para esta actualización existente. No registra ni arranca tareas.
4. Iniciar la tarea existente `QDNH-Servidor`; no ejecutar QDNH.exe por separado. Comprobar heartbeat con PID/fecha/secuencia reciente, recursos de audio/serie y listeners acordes al modo.
5. El modo tradicional se conserva. Para usar negociación, activar el modo compartido en una ventana de mantenimiento y comprobar permisos/puerto de control de la instalación, sin publicar interfaces de diagnóstico. Validar tres clientes, ceder/tomar control, desconexiones y recepción antes de probar TX.
6. Para revertir, detener limpiamente, usar `Restore-QdnhRelease.ps1` con el nombre anterior (última evidencia local: `20261024b`) y arrancar la tarea. La configuración actual se conserva; no restaurar configuración antigua sin revisarla.

La instalación anterior con guardian y audio en Session 0 se validó el 24 de octubre. Esta actualización no se ha desplegado ni se ha reiniciado NucBox durante su preparación. Encendido tras corte de corriente/BIOS sigue pendiente por decisión del usuario.
