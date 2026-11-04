# FreshWay Gateway

Gateway C# para sustituir el gateway Java de las impresoras Mettler-Toledo FreshWay. Los backends Java de hardware se mantienen.

## Archivos

- `FreshWayGateway.cs`: fuente original entregado, sin modificaciones.
- `gateway.ini`: configuración original entregada.
- `%zprd11.txt`: rutina Caché original que consume el servicio.
- [Guía de instalación v1.2 (PDF)](docs/FreshWay_Gateway_Installation_v1.2.pdf).
- [Fuente editable de la guía v1.2](docs/FreshWay_Gateway_Installation_v1.2.md).
- [PDF original v1.1, histórico](docs/original/FreshWay_Gateway_Installation%20%281%29.pdf).

La guía v1.2 incorpora `UrlSegmentMaxLength`, usa el nombre correcto **POSPrinterWS** para el servicio Java y explica cómo alternarlo con **FreshWayGateway**. El PDF histórico conserva su contenido original; utilizar v1.2 para instalar.

El fuente adjunto identifica su versión como 1.0 y utiliza `RawUrl`; no implementa el filtro de `JposException` mencionado en la guía histórica. No se modificó el comportamiento del servicio en esta entrega.

## Regenerar el PDF

Con Pandoc, XeLaTeX y las fuentes DejaVu instaladas, desde la raíz del repositorio:

```sh
pandoc docs/FreshWay_Gateway_Installation_v1.2.md --pdf-engine=xelatex \
  -V mainfont='DejaVu Sans' -V monofont='DejaVu Sans Mono' \
  -V 'monofontoptions=Scale=0.78' \
  -o docs/FreshWay_Gateway_Installation_v1.2.pdf
```

La compilación y ejecución del servicio requieren Windows y .NET Framework; consultar la guía para los comandos y las comprobaciones con la impresora real.
