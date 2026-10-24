# Acceso individual opcional

El valor vacío de `OperatorAccountsFile` mantiene exactamente el acceso compartido anterior. Para activar cuentas se necesita `SharedRadioMode=True`; una configuración inválida detiene la apertura de listeners, nunca vuelve silenciosamente al secreto compartido.

En la configuración `.conf` agregar el par de líneas:

```
OperatorAccountsFile
operator-accounts.json
```

La ruta se resuelve desde el directorio de trabajo (en el supervisor: `state/`). `config/operator-accounts.schema.json` describe el formato. `operator-accounts.example.json` es una plantilla sin secretos ni cuentas activas: sustituir los identificadores e indicativos, configurar las variables de entorno por un canal local seguro y guardar la copia autorizada en `state/`. El ejemplo no se activa al instalar paquetes.

Cada cuenta usa UserId estable, Callsign único, Role y SecretEnvironmentVariable. Los secretos individuales son distintos, de 16–128 caracteres ASCII visibles; no se guardan en JSON ni se imprimen. Los valores deben estar presentes en el entorno del proceso del servidor. Los clientes siguen usando el campo contraseña del perfil, ahora con su secreto individual, en los tres canales. No cambiar la codificación: el cliente histórico aplica ASCII antes de SHA256. No usar contraseñas Unicode para esta extensión.

- Observer: escucha audio/serie y consulta salud/operadores. No obtiene control, turno ni TX.
- Operator: toma control libre, solicita turno y TX, sin expulsar al propietario.
- Administrator: mismos permisos de radio que Operator; identificación administrativa preparada. Esta entrega no añade edición remota de cuentas, robo de control ni RPC de administración. La edición de cuentas exige acceso autorizado al equipo anfitrión.

HelloAck informa AuthenticatedUserId/Role y `operator-auth-v1` solo cuando hay cuentas configuradas. OperatorInfo.Role permite mostrar permisos. El indicativo procede de la cuenta del servidor, no del Hello del cliente. Todos los canales del mismo GUID deben autenticar la misma identidad. La vinculación se conserva mientras quede abierto cualquier canal; un ReHello no cambia identidad. Reabrir con otra identidad un GUID ya sin conexiones crea una sesión nueva, sin heredar permisos.

Para revocar una cuenta o rotar secretos, modificar la configuración/entorno autorizado y reiniciar o reinicializar el servidor durante una ventana de prueba. Se cierran las conexiones anteriores y se libera TX; no se mantiene autenticación antigua en sesiones nuevas. No se ha activado esta configuración en el NucBox.

## Transporte privado y dependencia pendiente

Esta extensión autentica y autoriza; no cifra audio, comandos ni metadatos TCP. La captura del reto/respuesta permite probar secretos fuera de línea, por lo que se requieren secretos largos y una red de acceso protegida. No añade certificados ni TLS improvisado.

Antes de ampliar acceso, elegir y comprobar una de estas opciones con la instalación real:

1. Acceso únicamente por LAN o red privada/VPN administrada: permitir los tres puertos solo desde los clientes/red privada previstos; comprobar bloqueo desde otras redes. Los móviles necesitan la ruta privada en sus perfiles.
2. Un diseño futuro de TLS integrado: acordar validación de certificados del servidor, transporte en los tres canales y migración/fallback explícitos en Android. No implementado ni anunciado como capacidad.

La primera opción es compatible con el software entregado, pero su elección, configuración y prueba en NucBox/móviles siguen pendientes. Ningún script cambia firewall, router, reenvío de puertos o VPN. No exponer el puerto TCP sin cifrado como si esta autenticación sustituyera la protección del transporte.

## Pruebas

`dotnet test QDNH.sln` incluye secretos correctos/incorrectos y reto no reutilizable con otra sal, roles, identidad en todos los canales, suplantación de indicativo/GUID, salida de control conservando audio y configuraciones duplicadas/incorrectas. Una prueba TCP autentica un secreto individual, verifica el Ack autorizado y comprueba cierre ante contraseña compartida incorrecta. Todos los secretos de las pruebas son constantes sintéticas sin uso operativo.
