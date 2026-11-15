# Referencia desplegada y reconciliación

Verificación por SSH el 15 de noviembre de 2026. QDNH seguía Running, PID 10904. No se reinició durante esta reconciliación.

- Versión del NucBox: `20261102-7d87ba3`.
- Commit: `7d87ba32ed8256af534a540bb6c80232d4741579`.
- Etiqueta: `installed/nucbox-20261102`.
- ProductVersion: `1.0.0+7d87ba32ed8256af534a540bb6c80232d4741579`.
- SHA256 de QDNH.exe leído del equipo: `04B9C744DABEB15BA21955B58B6E71E4859EF569FFF00BC28E518C00EF3180B5`.

La reconciliación une los historiales local y remoto c05c874. El árbol remoto e30390a es idéntico al local 6e71d4b. El cambio posterior de identidad del delegado es equivalente a 7d87ba3. Se conservan los avances locales de latencia y LCD y se incorpora MultiListenTests del remoto, además de las pruebas locales. Resultado: 87 pruebas aprobadas.

El commit reconciliado incluye una prueba adicional y este documento; la etiqueta identifica exactamente el código del ejecutable instalado. Los avisos de la sonda de salud quedan pendientes de una intervención separada.
