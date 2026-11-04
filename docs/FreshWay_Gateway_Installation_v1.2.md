---
title: FreshWay Gateway Service
subtitle: Guía de instalación, diagnóstico y cambio de gateway — Versión 1.2
author: Mettler-Toledo FreshWay · Documentación del proyecto
date: 4 de noviembre de 2026
lang: es
geometry: margin=22mm
fontsize: 10pt
colorlinks: true
---

# Alcance y arquitectura

Esta revisión actualiza la guía 1.1: incorpora `UrlSegmentMaxLength`, corrige el nombre del servicio Java a **POSPrinterWS** y documenta cómo alternar entre el gateway Java y el C# para recuperación y pruebas comparativas.

El nuevo servicio reemplaza solamente `gateway.jar` (Spring Boot). Los backends Java siguen siendo necesarios para comunicarse con el hardware mediante JavaPOS, PAL-Service y USB.

```text
Caché COS (%zprd11) -> HTTP :8082 -> UN gateway activo:
  Java: POSPrinterWS (gateway.jar)
  C#:   FreshWayGateway (FreshWayGateway.exe)
    -> :8083 MettlerWSV1.1_G.jar (Label / Green)
    -> :8084 MettlerWSV1.1_B.jar (Receipt / Linerless / Blue)
    -> JavaPOS -> PAL-Service -> USB -> impresora FreshWay
```

Ambos gateways utilizan el mismo puerto y las mismas rutas. No pueden estar activos simultáneamente en ese puerto. Caché no requiere cambios de código para alternarlos. Los puertos indicados son valores habituales: verificar los reales de cada instalación.

La ubicación habitual del servicio anterior y sus wrappers WinSW es:

```text
D:\sdppc\RestService\Mettler Driver gateway\
```

**Correspondencia con el código adjunto.** `FreshWayGateway.cs` conserva su identificación interna v1.0 y ya utiliza `ctx.Request.RawUrl`. No contiene el filtro de `JposException` que describía la guía anterior: no asumir esa función ni interpretar una respuesta vacía como éxito. Esta revisión modifica documentación, no el código del servicio. Las mejoras de latencia citadas en la guía original no se han vuelto a medir.

# Preparación y comprobaciones iniciales

Ejecutar los comandos en **CMD como administrador**. Si se utiliza PowerShell, escribir `sc.exe` para evitar el alias `sc`. Programar una pausa de impresión y el reinicio de Windows requerido por la configuración de HTTP.sys.

Antes de modificar la instalación, registrar los tipos de inicio y las rutas de los servicios:

```bat
sc.exe qc POSPrinterWS
sc.exe query POSPrinterWS
sc.exe qc FreshWayGateway
sc.exe query FreshWayGateway
sc.exe query Mettler_Driver_G
sc.exe query Mettler_Driver_B
wmic process where "name='java.exe'" get ProcessId,CommandLine
netstat -ano -p tcp | findstr ":8082"
```

Si FreshWayGateway todavía no está instalado, el error 1060 en su consulta es esperado. Si WMIC no está disponible, consultar los procesos con PowerShell:

```powershell
Get-CimInstance Win32_Process -Filter "name='java.exe'" |
  Select-Object ProcessId, CommandLine
```

Conservar el EXE, el INI, los JAR, los XML WinSW y los registros previos. No desinstalar **POSPrinterWS**: se necesita para volver al servicio anterior. Confirmar que su `BINARY_PATH_NAME` y configuración corresponden a `gateway.jar`.

## Identificación de servicios y backends

```bat
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.NET4.xml" | findstr "<id>"
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.G.xml" | findstr "<id>"
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.B.xml" | findstr "<id>"
```

El nombre del servicio gateway Java de esta instalación es **POSPrinterWS**. Los backends habituales son **Mettler_Driver_G** y **Mettler_Driver_B**; comprobarlos en el equipo. Si solo hay una impresora, puede estar instalado o habilitado únicamente el backend correspondiente. No habilitar módulos inexistentes.

## .NET Framework

```bat
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release
```

Se requiere .NET Framework 4.7 o posterior (`Release >= 460798`). La compilación utiliza el compilador del Framework; su carpeta continúa llamándose `v4.0.30319`.

## Reserva de URL de HTTP.sys

Consultar primero la reserva existente:

```bat
netsh http show urlacl url=http://+:8082/
```

Si falta, crearla para la cuenta que ejecutará el servicio. Con la configuración predeterminada de servicio LocalSystem:

```bat
netsh http add urlacl url=http://+:8082/ user="NT AUTHORITY\SYSTEM"
```

Si se usa otra cuenta, reemplazarla por esa cuenta. La guía original concedía acceso a EVERYONE; no es necesario ampliar permisos si la cuenta del servicio ya tiene acceso. Para una prueba de consola, la cuenta que la ejecuta también debe tener permisos; usar CMD elevado. No eliminar reservas existentes sin revisar quién las utiliza.

# Configuración obligatoria de HTTP.sys

`HttpListener` recibe las solicitudes a través de HTTP.sys. Caché incluye comandos ESC (`%1B`), retornos de carro (`%0D`), LF (`%0A`) y SOH (`%01`) codificados dentro de las rutas de impresión. Además, el texto completo de ingredientes se envía en **un único segmento** después de `/pos/p_printnormal/2/`.

Estas configuraciones son globales para HTTP.sys en el equipo, no exclusivas de FreshWayGateway. Registrar los valores originales —incluido si no existían— antes de cambiarlos:

```bat
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v AllowRestrictedChars
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v UrlSegmentMaxLength
```

## Permitir caracteres restringidos codificados

```bat
reg add "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" ^
  /v AllowRestrictedChars /t REG_DWORD /d 1 /f
```

Este ajuste permite caracteres restringidos codificados que necesita el protocolo existente. No sustituye los límites de longitud.

## Ampliar el largo máximo de un segmento de URL

El valor predeterminado de `UrlSegmentMaxLength` es 260 caracteres. Los ingredientes extensos pueden superarlo aunque sus líneas estén separadas con `%0D`: los retornos de carro no crean nuevos segmentos de URL.

Para esta instalación se establece un límite de 4096:

```bat
reg add "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" ^
  /v UrlSegmentMaxLength /t REG_DWORD /d 4096 /f
```

Si ya existe un valor superior por requerimiento de otra aplicación, conservarlo y documentarlo en lugar de reducirlo. Este parámetro no amplía otros límites de longitud de solicitud. Si la etiqueta más extensa continúa fallando, investigar el rechazo concreto antes de modificar otros límites.

## Reiniciar y verificar

Aplicar ambos ajustes en una misma ventana de mantenimiento. Antes del reinicio, asegurar que solo el gateway elegido tenga inicio automático; la sección de cambio de gateway explica cómo hacerlo. Reiniciar Windows de forma controlada:

```bat
shutdown /r /t 60
```

Guardar el trabajo antes de ejecutar el comando. Después del reinicio:

```bat
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v AllowRestrictedChars
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v UrlSegmentMaxLength
```

Esperado: `AllowRestrictedChars = 0x1` y `UrlSegmentMaxLength = 0x1000` (4096 decimal), o el límite superior previamente conservado. Reiniciar únicamente el servicio C# no aplica estos ajustes del kernel.

# Compilación e instalación del gateway C#

## Preparar y compilar

Copiar el fuente y el INI a la carpeta de trabajo:

```bat
mkdir C:\FreshwayPrinterService
cd /d C:\FreshwayPrinterService
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" ^
  /target:exe /platform:x64 ^
  /r:System.Web.dll /r:System.ServiceProcess.dll ^
  /out:FreshWayGateway.exe FreshWayGateway.cs
```

La compilación debe finalizar sin errores. Si ya existe un servicio C# activo, detenerlo y esperar `STOPPED` antes de reemplazar su ejecutable. Editar el `.cs` no modifica el binario desplegado: recompilar, copiar el EXE y reiniciar el servicio después de cualquier cambio de código.

## Configurar gateway.ini

Debe estar junto al EXE. Usar comentarios en líneas separadas: el parser adjunto no interpreta comentarios después de los valores numéricos.

```ini
; Puerto que utiliza Cache
port=8082
; Backend Label / Green
port_green=8083
; Backend Receipt / Linerless / Blue
port_blue=8084
backend_timeout=30000
```

Verificar los puertos con las líneas de comando de los JAR y con la configuración de impresora de Caché. No cambiar el puerto de Caché entre pruebas comparativas si ambos gateways escuchan en 8082.

## Registrar el servicio C# sin iniciarlo todavía

Si no existe:

```bat
sc.exe create FreshWayGateway ^
  binPath= "C:\FreshwayPrinterService\FreshWayGateway.exe" ^
  DisplayName= "FreshWay Gateway Service" ^
  start= demand
sc.exe description FreshWayGateway "Gateway HTTP C# para impresoras FreshWay"
sc.exe failure FreshWayGateway reset= 60 actions= restart/5000/restart/10000/restart/30000
```

Si ya existe, comprobar `sc.exe qc FreshWayGateway` y su ruta en lugar de volver a crearlo. Mantenerlo detenido hasta liberar 8082. Aplicar los ajustes de HTTP.sys y realizar el reinicio previsto antes de validar ingredientes.

# Alternar entre el servicio nuevo y el anterior

Esta sección sirve tanto para recuperar la impresión como para comparar resultados. Mantener los backends necesarios ejecutándose durante ambos modos. No detener PAL-Service ni los servicios G/B para cambiar únicamente de gateway.

## Reglas antes de cada cambio

1. Pausar nuevos trabajos de Caché y dejar finalizar la impresión en curso. Conservar la etiqueta de prueba y los logs. No cambiar de gateway a mitad de una transacción.
2. Registrar qué servicio está activo y su tipo de inicio con `sc.exe query` y `sc.exe qc`.
3. Detener el gateway saliente y esperar realmente `STATE : 1 STOPPED`. `sc.exe stop` es asíncrono; repetir la consulta mientras esté `STOP_PENDING`. No continuar si no se detiene.
4. Si FreshWayGateway se ejecuta con `--console`, detener también esa instancia con Enter en su consola. Detener el servicio no detiene una instancia de consola separada.
5. Comprobar el puerto antes de iniciar el otro gateway:

```bat
netstat -ano -p tcp | findstr ":8082"
```

No debe existir un listener en el puerto local 8082. Las conexiones `TIME_WAIT` no son listeners. Identificar un PID inesperado con `tasklist /fi "PID eq NUMERO"`; HTTP.sys puede aparecer como PID 4. En ese caso consultar `netsh http show servicestate` para identificar la aplicación registrada: no intentar terminar el proceso System.

## Volver al servicio anterior: C# -> Java (POSPrinterWS)

Ejecutar por etapas, verificando cada estado:

```bat
REM Evitar que C# vuelva a iniciarse al reiniciar Windows
sc.exe config FreshWayGateway start= disabled
sc.exe stop FreshWayGateway
sc.exe query FreshWayGateway
```

Esperar `STOPPED`, detener cualquier consola C# y comprobar que 8082 quedó libre según el procedimiento anterior. Luego:

```bat
sc.exe config POSPrinterWS start= auto
sc.exe start POSPrinterWS
sc.exe query POSPrinterWS
sc.exe qc POSPrinterWS
```

Esperar `RUNNING`; el gateway Spring Boot puede tardar varios segundos adicionales en escuchar. Confirmar listener en 8082, backends necesarios en `RUNNING` y proceso `gateway.jar` activo. La configuración estable de este modo es:

- **POSPrinterWS:** automático y ejecutándose.
- **FreshWayGateway:** deshabilitado y detenido, sin instancia de consola.
- **Mettler_Driver_G / Mettler_Driver_B:** sin cambios, según hardware instalado.

Reanudar con una impresión completa desde Caché: una etiqueta corta y la misma etiqueta extensa de ingredientes. Verificar contenido, saltos de línea, código de barras y avance/corte. No basta con que el servicio esté `RUNNING`. Si hay estado residual del intento fallido, cerrar y reinicializar la sesión de impresora desde el flujo normal de Caché antes de comparar.

No es necesario eliminar el EXE nuevo, desinstalar el servicio C#, quitar la reserva de URL ni revertir HTTP.sys para volver a Java. Mantener esos ajustes facilita alternar nuevamente.

## Volver al servicio nuevo: Java -> C# (FreshWayGateway)

Con la impresión pausada:

```bat
sc.exe config POSPrinterWS start= disabled
sc.exe stop POSPrinterWS
sc.exe query POSPrinterWS
```

Esperar `STOPPED` y puerto 8082 libre. Confirmar que HTTP.sys ya fue configurado y Windows reiniciado. Luego:

```bat
sc.exe config FreshWayGateway start= auto
sc.exe start FreshWayGateway
sc.exe query FreshWayGateway
sc.exe qc FreshWayGateway
```

Esperar `RUNNING`, comprobar listener y backends, y repetir las mismas impresiones completas desde Caché. La configuración estable de este modo es:

- **FreshWayGateway:** automático y ejecutándose.
- **POSPrinterWS:** deshabilitado y detenido.
- **Backends G/B:** sin cambios.

Si C# falla al iniciar o imprimir, conservar el error y ejecutar la sección de retorno a POSPrinterWS. No iniciar ambos servicios para intentar recuperar el puerto.

## Prueba temporal del C# en consola

Detener y deshabilitar POSPrinterWS y detener el servicio FreshWayGateway; esperar ambos detenidos y 8082 libre. Mantener el servicio C# en inicio manual durante la prueba:

```bat
sc.exe config FreshWayGateway start= demand
cd /d C:\FreshwayPrinterService
FreshWayGateway.exe --console
```

Observar solicitudes mientras Caché imprime. Al terminar, pulsar Enter, verificar que el puerto se liberó y elegir uno de los dos procedimientos anteriores para dejar un único servicio automático. No dejar una prueba de consola como configuración permanente.

## Registro de pruebas comparativas

Utilizar el mismo producto, texto completo, diseño, impresora, backends y configuración de Caché. Ejecutar los trabajos secuencialmente, nunca de manera simultánea.

Registrar por gateway: fecha/hora, binario desplegado, puertos, valores HTTP.sys, resultado de etiqueta corta y extensa, saltos de línea, errores HTTPERR, respuestas de Caché y tiempos observados. Conservar los logs antes de repetir. Si se modificó HTTP.sys entre pruebas, anotarlo: ya no son condiciones idénticas.

## Restauración opcional de HTTP.sys

No forma parte del cambio normal de gateway. Si se retira definitivamente el gateway C# y se decide restaurar HTTP.sys, revisar primero el impacto en otras aplicaciones y utilizar exactamente los valores registrados antes de la instalación. Si un valor no existía, eliminar únicamente ese valor; si existía, restaurar su dato y tipo original. No importar indiscriminadamente toda la rama del registro. Programar otro reinicio de Windows para aplicar la restauración.

# Validación después de instalar o cambiar de gateway

```bat
sc.exe query POSPrinterWS
sc.exe query FreshWayGateway
sc.exe query Mettler_Driver_G
sc.exe query Mettler_Driver_B
netstat -ano -p tcp | findstr ":8082"
```

Debe quedar activo solo el gateway elegido. Un backend ausente por hardware no instalado no es por sí solo un error.

Realizar desde Caché una secuencia completa de apertura, claim, enable, impresión y cierre. Comprobar físicamente una etiqueta corta y la etiqueta extensa que antes producía `400 - URL -`. En modo C#, el `p_printnormal` extenso debe llegar al log del gateway y no generar un nuevo rechazo HTTPERR. Confirmar los CR internos y los caracteres acentuados; no sustituir `$c(13)` por `$c(10)` para ocultar un rechazo HTTP.

Una llamada aislada a `p_open` solo valida apertura: puede cambiar el estado o la ruta activa del gateway y no demuestra que la impresión completa funcione. Evitar mezclar pruebas manuales con trabajos reales.

# Configuración por tipo de impresora

| Dispositivo de p_open | Puerto predeterminado | Backend |
|---|---:|---|
| LabelPrinter-1 | 8083 | Green |
| LabelPrinter-1-Sim | 8083 | Green, simulador |
| ReceiptPrinter-1 | 8084 | Blue |
| LinerlessPrinter-1 | 8084 | Blue |

En Caché: MTA#1 corresponde a Receipt, MTA#2 a Label y MTA#3 a Linerless. Configurar únicamente los dispositivos presentes y verificar sus entradas `jpos.xml`. No redirigir una impresora inexistente a otro backend como sustituto de su instalación.

Para agregar Label o Linerless, verificar módulo físico, alimentación y paquetes UPOS correspondientes. Para Label, revisar el paquete FLP y su registro:

```bat
reg query "HKLM\SOFTWARE\OLEforRetail\ServiceOPOS\POSPrinter" /s
```

Confirmar `LabelPrinter-1` en la configuración apropiada; para Linerless confirmar `LinerlessPrinter-1` en el backend FP. Habilitar Green para Label o Blue para Receipt/Linerless según la tabla. Conservar los puertos reales en `gateway.ini`.

Si el driver USB fue reinstalado o eliminado, seguir el procedimiento del instalador Mettler y los INF específicos del hardware. La guía original menciona `LabelPrinterFLP2.inf` para VID_0EB8/PID_300C y `LabelPrinterFLP01.inf` para PID_3014; verificar modelo y paquete antes de instalarlos. No reinstalar drivers para diagnosticar un `400 - URL -`, que ocurre antes de acceder al hardware.

# Diagnóstico

## HTTPERR: 400 - URL - sin entrada en el gateway C#

```text
C:\Windows\System32\LogFiles\HTTPERR\httperr*.log
```

HTTP.sys rechazó la solicitud antes de `Handle`. Revisar `AllowRestrictedChars`, `UrlSegmentMaxLength` y el reinicio posterior. En la solicitud de ingredientes observada hay `%1B`, varios `%0D` y un segmento superior al límite predeterminado. El motivo genérico `URL` no distingue por sí solo entre todas las validaciones: confirmar con la misma etiqueta después de aplicar los ajustes.

## La solicitud sí aparece en el gateway, pero no imprime

Revisar puertos, procesos Java, respuestas completas y logs del backend. El C# adjunto utiliza RawUrl, pero al crear la solicitud saliente también interviene el procesamiento de URI de .NET. Verificar lo que recibe Java antes de atribuir automáticamente cualquier error a una versión antigua.

El código adjunto no propaga el estado HTTP del backend al cliente y recorta los mensajes de consola. Un error de Java puede llegar a Caché como HTTP 200 con un cuerpo de error o vacío. No tomar HTTP 200 ni ausencia de texto como prueba de impresión exitosa; conservar los logs del backend. Una mejora de manejo de errores corresponde a una revisión de código separada.

## Puerto ocupado o servicio que no arranca

Comprobar que el otro gateway y cualquier instancia `--console` estén detenidos. Revisar `sc.exe qc`, ruta del EXE, reserva URL, cuenta de servicio y eventos de Windows. Esperar `STOPPED` antes de iniciar el reemplazo. Si POSPrinterWS no inicia al regresar, revisar los logs de su wrapper WinSW, su Java y su configuración original.

## Conexión rechazada al backend

Confirmar que el backend del dispositivo está instalado, en `RUNNING` y escuchando en el puerto configurado. Mantener Green/Blue activos al alternar gateways; no cambiar drivers USB por un error de conexión HTTP.

## Todas las llamadas parecen correctas pero la etiqueta está vacía

Verificar secuencia completa, modo página, estado de la transacción, logs JavaPOS/UPOS y PAL-Service. La guía original cita `D:\sdp_mta_prn_SCA01.txt`; confirmar la ruta real del equipo. Comparar con POSPrinterWS usando exactamente el mismo diseño y producto.

En Caché, consultar `zw ^posfwlog` y conservar su contenido antes de limpiar o repetir. No borrar los logs como paso de instalación.

# Historial y archivos

- **1.2 — 4 de noviembre de 2026:** UrlSegmentMaxLength=4096, servicio Java POSPrinterWS, retorno completo a Java, reactivación de C#, pruebas comparativas y validación de ingredientes. Se aclaran diferencias entre fuente y guía 1.1 y se corrige el ejemplo de INI para evitar comentarios inline.
- **1.1 — documento original:** RawUrl y requisito AllowRestrictedChars. El filtro de excepciones mencionado allí no está implementado en el fuente adjunto.

Los cuatro archivos entregados originalmente se conservan sin modificar en el repositorio. El PDF original es histórico y puede contener nombres anteriores; para nuevas instalaciones utilizar esta revisión 1.2. El Markdown de esta revisión es la fuente editable del nuevo PDF.
