# Host Windows de prueba, copia y reversión

Los scripts de `scripts/host/` preparan una instalación paralela. No sustituyen por sí solos la instalación activa ni seleccionan dispositivos. Requieren PowerShell de Windows y un paquete publicado con QDNH.exe. La primera instalación exige copia explícita de la configuración actual ya identificada; no se incluye ninguna configuración real ni credencial.

Estructura: `releases/<versión>/` conserva cada paquete; `state/default.conf` conserva los ajustes; `current.json` selecciona versión; `backups/*.zip` guarda referencia/configuración antes de actualizar o revertir; `logs/` guarda el supervisor. Proteger HostRoot y sus copias con permisos del usuario/administradores autorizados: una configuración antigua puede contener la contraseña compartida. Las variables de entorno de cuentas no se exportan al backup. No copiar secretos al repositorio.

## Preparación sin arrancar radio

`Install-QdnhRelease.ps1 -SourcePackage <paquete> -HostRoot <carpeta-de-prueba> -ReleaseName <versión> -InitialConfig <copia.conf> -WhatIf` permite revisar el destino. Ejecutar después sin WhatIf instala, conserva estado y cambia referencia; no arranca QDNH. En actualizaciones no es necesario InitialConfig. Una versión existente nunca se sobrescribe. No usar el directorio de la instalación activa como HostRoot de pruebas.

Identificar primero ejecutable/configuración activos y guardar una copia independiente. Detener el QDNH anterior antes de iniciar la versión de prueba: los scripts no detectan instalaciones externas a HostRoot ni pueden verificar que la radio esté desocupada.

## Supervisión en sesión del usuario

`Start-QdnhHost.ps1 -HostRoot <carpeta> -Resume` arranca en el usuario conectado, mantiene stdin abierto para evitar bucles de EOF y registra stdout/stderr. Por defecto rechaza Session 0; las opciones explicitas de prueba estan documentadas en Guardian-y-arranque.md. Reinicia salidas con error hasta MaximumRestarts (5 por defecto) con espera creciente; salida normal no se reinicia. No confirma que WASAPI/AIOC estén operativos: comprobar audio y radio en esa misma sesión, bloqueo de pantalla y desconexión de usuario antes de uso diario.

`Stop-QdnhHost.ps1 -HostRoot <carpeta>` solicita Q y espera salida limpia. En parada manual, si no responde, el supervisor mantiene la espera para revisar PTT/hardware. Ante bloqueo detectado automaticamente se permite terminar solo la instancia supervisada y relanzarla, segun Guardian-y-arranque.md. El cerrojo y la identidad PID/fecha impiden lanzar otra instancia supervisada o actualizar mientras ese proceso siga activo. Un QDNH abierto fuera de estos scripts exige comprobación manual.

`Register-QdnhLogonTask.ps1 -HostRoot <carpeta> -TaskName QDNH-Pruebas -WhatIf` prepara una tarea al iniciar sesión del usuario actual. Sin WhatIf registra la tarea, sin arrancarla ahora. Usa LogonType Interactive y privilegios normales; no almacena contraseñas, no activa inicio automático de Windows ni instala un servicio. Si el usuario cierra sesión, la continuidad de audio no está garantizada. No registrar sobre una tarea existente. Para retirar la tarea, usar el Programador de tareas con su nombre exacto después de detener el host.

## Reversión

Detener el supervisor y confirmar proceso finalizado. Ejecutar `Restore-QdnhRelease.ps1 -HostRoot <carpeta> -ReleaseName <versión-anterior>`. Guarda nueva copia y cambia solo la referencia; mantiene la configuración actual, no arranca ni transmite. Si también hay que volver a una configuración antigua, inspeccionar el ZIP correspondiente y restaurar explícitamente la copia autorizada con el host detenido. No restaurar contraseñas antiguas por defecto. Las versiones previas se conservan; no hay borrados recursivos.

Los logs y backups se conservan para diagnóstico. Revisar retención/tamaño del disco en la puesta en servicio; esta primera entrega no elimina automáticamente evidencias.

## Comprobación reproducible sin hardware

`Test-HostScripts.ps1 -ScratchRoot <carpeta-temporal>` comprueba WhatIf, instalación paralela, backup, conservación byte a byte de configuración, reversión, rutas inválidas, bloqueo activo y sintaxis. Conserva el sandbox para inspección.

Para comprobar además supervisión/stdin/parada, publicar `scripts/host/fixtures/ConsoleHost/Fixture.csproj` en un directorio temporal y pasar ese directorio con `-SupervisorFixture <directorio>`. Es un programa de consola que solo espera Q: no referencia radio ni audio. La prueba arranca ese doble como proceso oculto, verifica que sigue vivo con stdin abierto, solicita parada y comprueba su salida. No registra tareas programadas. No pasar el paquete real como SupervisorFixture.

Pendientes de despliegue real: identificar/guardar instalación NucBox, validar puertos/dispositivos, aplicar decisión de acceso privado y secretos por un canal autorizado, probar PTT y dos móviles, validar sesión prolongada/WASAPI y dejar selección de versión anterior comprobada.


La vigilancia de progreso, modo headless, reinicio por heartbeat congelado y alternativas de arranque Windows estan detallados en Guardian-y-arranque.md. La prueba de consola ampliada verifica una primera instancia viva pero congelada, su sustitucion y salida limpia de la segunda.
