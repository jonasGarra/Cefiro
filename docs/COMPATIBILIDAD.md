# Compatibilidad comprobada en tu PC (Fase 0)

> Resultado de `tools/Cefiro.HwProbe` ejecutado el 22-11-2026 como administrador y como SYSTEM
> (el contexto de un servicio de Windows), con LibreHardwareMonitorLib **0.9.7-pre785**,
> driver **PawnIO 2.2.0** y **OpenRGB 1.1** (servidor SDK, protocolo 6).
> Leyenda: ✅ comprobado en tu PC · ⚠️ funciona con matices · ❌ no se puede.

## Tabla definitiva

| Componente | LEER (sensores) | CONTROLAR VENTILADOR | CONTROLAR RGB | Librería | Notas |
|---|---|---|---|---|---|
| **CPU Ryzen 7 7700X** | ✅ Tctl/Tdie, CCD1, carga total y por hilo, relojes por núcleo, consumo del paquete y por núcleo | — | — | LHM + PawnIO | |
| **GPU RTX 5070 (ASUS)** | ✅ núcleo, hot spot (6 puntos), memoria, carga, relojes, consumo, RPM de 2 ventiladores | ✅ **probado**: 35 % → ~935 RPM, 70 % → ~2115 RPM y vuelta al modo automático (0 RPM en reposo) | ❌ OpenRGB no detecta iluminación en esta tarjeta | LHM (NvAPI) | Mínimo admitido por la tarjeta: **30 %**. 0 RPM en reposo es normal |
| **Placa MSI X670E (NCT6687D)** | ✅ CPU, sistema, VRM MOS, chipset (PCH), zócalo, PCIe x1, voltajes (12 V, 5 V, Vcore, DIMM…) | ✅ **probado** en SYS_FAN4: 35 % → 743 RPM, 70 % → 1494 RPM y vuelta a la curva de la BIOS (51 %) | — | LHM + PawnIO | Nombres de LHM ↔ conector: CPU Fan = CPU_FAN1, Pump Fan = PUMP_FAN1, System Fan #n = SYS_FANn |
| **Placa: RGB (Mystic Light 185 bytes)** | — | — | ✅ detectada con **modo "Direct"** LED a LED y 23 modos propios. Zonas: JRGB1 y JRGB2 (12 V), **JRAINBOW1** (hasta 200 LEDs) y **JRAINBOW2** (hasta 240 LEDs) | OpenRGB 1.1 | Los JRAINBOW están a 0 LEDs: hay que configurar cuántos tiene cada cadena (Fase 3). Falta la prueba visual |
| **9 × RS120 ARGB** | ✅ RPM por conector | ✅ por conector | ✅ por LED a través de JRAINBOW1/2 (8 LEDs por ventilador) | LHM / OpenRGB | Solo SYS_FAN4 tiene ventiladores de caja: **parecen estar los 6 en cadena en un único SYS_FAN** |
| **Bomba Nautilus 360 RS** | ✅ RPM | ⚠️ protegida: nunca baja del 60 % ni se para | ✅ por la misma cadena ARGB | LHM | **Está en CPU_FAN1** (2521 RPM con un 54 %, más de lo que puede dar un RS120) |
| **Radiador (3 × RS120)** | ✅ RPM | ✅ con mínimo del 30 % | ✅ | LHM | **Está en PUMP_FAN1** (1102 RPM con un 53 %) |
| **RAM Vengeance RGB DDR5 ×2** | ✅ temperatura de cada módulo (30,5 °C y 28 °C) | — | ✅ detectadas las 2 (SMBus PIIX4, direcciones 0x19 y 0x1B), 10 LEDs cada una, modo "Direct" | OpenRGB + PawnIO | Falta la prueba visual (Fase 3). iCUE no puede estar abierto a la vez |
| **Caja FRAME 4000D RS** | — | — | — | — | Su iluminación son los ventiladores |

## Decisiones tomadas tras la prueba

1. **LibreHardwareMonitorLib 0.9.7-pre785 en vez de 0.9.6.** La 0.9.6 estable tiene un fallo
   (corregido en el PR #2527, 18-09-2026): al devolver un ventilador del NCT6687D a la BIOS lo dejaba
   en manual. La versión preliminar ya lo trae corregido y lo he comprobado en tu SYS_FAN4.
2. **Segunda capa de seguridad al devolver el control.** Antes de soltar un conector, Céfiro fuerza en
   LHM el estado inicial "automático" del NCT6687D. Así, aunque un proceso anterior se cerrara de golpe,
   el ventilador vuelve siempre a la curva de la BIOS. Comprobado: SYS_FAN4 volvió de 70 % a la curva (51 %).
3. **La bomba y el radiador se identifican por sus RPM, no por el nombre del conector.** En tu PC
   la bomba está en CPU_FAN1 y el radiador en PUMP_FAN1. Al revés de lo que diría el nombre.
4. **Cliente OpenRGB propio con protocolo 6** (OpenRGB.NET solo llega al 4): conectado y validado
   contra tu servidor OpenRGB 1.1. Los IDs de los dispositivos cambian al redetectar, así que Céfiro los
   reconoce por su nombre y ubicación (USB/SMBus), no por el ID.
5. **El núcleo funciona como SYSTEM**: el diagnóstico como servicio leyó el chip de la placa, la GPU y
   sus controles sin problemas.

## Cosas que conviene revisar en tu PC

- ⚠️ **Corriente en SYS_FAN4**: si los 6 ventiladores de la caja están en ese único conector, pueden
  sumar hasta 6 × 0,4 A = 2,4 A. Los conectores SYS_FAN de MSI suelen estar pensados para 1 A.
  Compruébalo en el manual y, si puedes, reparte la cadena entre varios SYS_FAN (tienes 5 libres).
- La **Iluminación dinámica** de Windows sigue activada: apágala (Configuración → Personalización →
  Iluminación dinámica) para que no compita con Céfiro.
- El **servicio de Mystic Light** de MSI Center está parado de forma temporal. Volverá a arrancar si
  reinicias. En la Fase 5, Céfiro avisará si lo detecta.

## Fase 2: control de ventiladores probado en tu PC (22-11-2026)

| Prueba | Resultado |
|---|---|
| SYS_FAN4 fijo al 30 % / 80 % | 651 RPM / 1706 RPM, con rampas suaves (bajada 3 %/s, subida 10 %/s) ✅ |
| Bomba (CPU_FAN1) pidiendo 20 % | El servicio la dejó en **60 %** ("ajustado por seguridad"), 2608 RPM ✅ |
| Radiador (PUMP_FAN1) pidiendo 10 % | El servicio lo dejó en **30 %** ✅ |
| Modo curva (máx. de CPU y GPU) | Sigue la temperatura con histéresis y rampa ✅ |
| **Emergencia real** (límite 60 °C + carga en 16 hilos) | Todo al 100 % en 1 s: bomba 3150, radiador 2200, caja 2120 y **GPU 3200 RPM**. Salió sola 15 s después de bajar la temperatura ✅ |
| Parada ordenada del servicio | SYS_FAN4 volvió del 30 % a la curva de la BIOS (51 %) ✅ |
| **Cierre brusco** (proceso matado) | El chip se quedó en el 30 %, como cabía esperar; al volver a arrancar, Céfiro lo devolvió a la BIOS (52 %) ✅ |

### Calibración automática (datos reales)

| Salida | 100 % | 60 % | 30 % | Se para | Arranca |
|---|---|---|---|---|---|
| SYS_FAN4 (6 × RS120 de la caja) | 2117 RPM | 1274 RPM | 640 RPM | por debajo del **15 %** | desde el **20 %** |
| PUMP_FAN1 (radiador, 3 × RS120) | 2107 RPM | 1269 RPM | 641 RPM | — (nunca baja del 30 %) | — |
| CPU_FAN1 (bomba Nautilus) | 3154 RPM | 2605 RPM | — | — (nunca baja del 60 %) | — |

La bomba sí responde a la señal PWM (de 2605 a 3154 RPM), así que el límite del 60 % tiene efecto real.

## Fase 3: iluminación probada en tu PC (22-11-2026)

Todo con el cliente propio del SDK (protocolo 6) contra el servicio de OpenRGB 1.1, **sin permisos de administrador**.

| Prueba | Resultado |
|---|---|
| Color fijo en todo | **Todo en cian**: ventiladores de la caja, líquida (bomba y ventiladores) y las dos RAM ✅ (confirmado por ti) |
| Mapa de conectores | **JRAINBOW1 = ventiladores de la caja** (3 frontales y 1 trasero) · **JRAINBOW2 = líquida** (bomba y 3 ventiladores de arriba) |
| LEDs por cadena | Las cadenas RS van **en paralelo**: cada ventilador (y la bomba) repite los mismos **8 LEDs**. Los dos conectores se configuran a 8 |
| Arcoíris animado (30 fps) | Fluido en ventiladores, bomba y RAM ✅ (confirmado por ti) · Céfiro gasta **0,18 % de CPU** |
| Efecto Música | Corregido el envío irregular (15 → 30 espectros/s). Prueba automática de punta a punta: el LED de graves pasa de 18 a 278 de brillo con sonido ✅. Confirmado por ti con música real |
| Modos propios del dispositivo | "Rainbow" en la RAM y "Rainbow wave" en la placa (se elige el nombre parecido) ✅ |
| Redetección / reinicio de OpenRGB | Céfiro detecta los nuevos IDs y vuelve a aplicar el modo ✅ |

### Qué no se puede hacer (por cómo está cableado)

- **Un color distinto en cada ventilador de una misma cadena**: van en paralelo y reciben la misma señal. Lo que sí
  puedes es tener un efecto para la **caja**, otro para la **líquida** y otro para **cada módulo de RAM**.
  Para colores por ventilador harían falta un hub ARGB con salidas independientes o un ventilador por conector,
  y la placa solo tiene 2 conectores ARGB.
- La gráfica no tiene iluminación controlable.

### Recomendación

**MSI Center · Mystic Light** (su proceso LEDKeeper2) sigue arrancando con Windows. Ahora mismo no ha interferido,
pero puede volver a poner sus colores tras reiniciar o suspender. Desinstala el módulo Mystic Light en
MSI Center (Características → Mystic Light → Desinstalar). Céfiro te avisará mientras siga activo.
