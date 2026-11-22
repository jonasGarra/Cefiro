# Plan: app de control de iluminación RGB y ventiladores

> Nombre: **Céfiro** (en el código, `Cefiro`, sin tilde).
> Fecha: 22-11-2026. Estado: **aprobado** el 22-11-2026.

---

## 0. Tu equipo (detectado en tu PC, no supuesto)

| Componente | Lo que he detectado |
|---|---|
| Placa base | MSI **X670E GAMING PLUS WIFI** (MS-7E16), BIOS 1.90 (12/2024) |
| CPU | AMD **Ryzen 7 7700X** (8 núcleos / 16 hilos) |
| GPU | NVIDIA **GeForce RTX 5070** de ASUS (PCI 10DE:2F04, subsistema 1043:89E6), driver 610.88 |
| RAM | 2 × **Corsair Vengeance RGB DDR5** 16 GB (CMH32GX5M2B6000Z30), a 6000 MT/s |
| Refrigeración líquida | Corsair **Nautilus 360 RS ARGB**: bomba + 3 × RS120 ARGB en el radiador |
| Caja | Corsair **FRAME 4000D RS ARGB**: trae 3 ventiladores RS ARGB |
| Ventiladores extra | 3 × Corsair **RS120 ARGB** (pack triple) |
| Controlador RGB de la placa | Dispositivo USB HID **0DB0:0076** ("MSI Mystic Light") |
| Chip de sensores/ventiladores | Nuvoton **NCT6687D** (es el que usa LibreHardwareMonitor en placas MSI AM5; se confirma en la Fase 0) |
| Sistema | Windows 11 Pro 64 bits (build 26200) |
| Herramientas | git 2.52 y winget sí; **no hay SDK de .NET** (solo el runtime 3.1) |

En total son **9 ventiladores RS120 ARGB + 1 bomba**. Ninguno tiene chip propio. Son ARGB estándar de 5 V y PWM de 4 pines, y van a los cabezales de la placa. **No tienes ningún hub Corsair** (ni iCUE LINK ni Commander), así que iCUE nunca ha controlado tus ventiladores ni tu líquida; solo la RAM.

---

## 1. Tabla de compatibilidad

Leyenda: ✅ funciona según el código o la documentación de la librería · ⚠️ probable, pero hay que comprobarlo en tu PC (Fase 0) · ❌ no se puede · — no aplica

| Componente | LEER (sensores) | CONTROLAR VENTILADOR | CONTROLAR RGB | Librería | Limitaciones |
|---|---|---|---|---|---|
| **CPU Ryzen 7 7700X** | ✅ temperatura (Tctl/Tdie y CCD), uso, frecuencia y consumo | — (su refrigeración es el radiador del CPU_FAN1, ver más abajo) | — | LibreHardwareMonitorLib 0.9.6 + PawnIO | Necesita administrador y el driver PawnIO instalado |
| **GPU RTX 5070 (ASUS)** | ✅ temperatura, uso, relojes, consumo y RPM | ⚠️ sí, con la API NvAPI "ClientFanCoolers" integrada en LHM, que además sabe devolver el control al modo automático | ❌ / sin luces: las páginas de ASUS de la PRIME RTX 5070 no anuncian iluminación, y el subsistema 89E6 no está en la lista de gráficas ASUS de OpenRGB | LHM (NvAPI). Plan B: NVML | LHM no ofrece el "hot spot" en las RTX 50. A 0 % de ventilador en reposo es normal (modo 0 RPM). Hay que comprobar que el control funciona desde un servicio de Windows |
| **Placa: sensores (NCT6687D)** | ✅ temperaturas de placa, VRM (MOS) y chipset, voltajes y RPM de los 8 cabezales | ⚠️ control PWM de CPU_FAN1, PUMP_FAN1 y SYS_FAN1–6. El código de LHM dice que en NCT6687D "antiguos" funciona y en X870/Z890 solo a medias. Tu X670E es de los antiguos | — | LHM + PawnIO | Tu placa no está en la lista de modelos de LHM: los sensores salen con nombres genéricos ("Fan #1"…) y los renombraremos tras identificarlos |
| **Placa: RGB (Mystic Light USB 0DB0:0076)** | — | — | ⚠️ OpenRGB 1.1 registra 0DB0:0076 como "MSI Mystic Light Common" (protocolo de 185 bytes). No hay confirmación para tu modelo exacto, y hay un aviso de la B650 Gaming Plus WiFi (mismo controlador) en que no se detectaba | OpenRGB 1.1 (servidor SDK) | **Es el punto crítico**: de él depende el RGB de los 9 ventiladores y de la bomba. MSI Center tiene que estar cerrado, porque se pelean por el mismo USB |
| **9 × RS120 ARGB** | ✅ RPM, pero **una lectura por cabezal**: en cadena solo llega un tacómetro | ✅ por cabezal (PWM, 420–2100 RPM, tienen modo 0 RPM) | ✅ por LED a lo largo de la cadena (8 LEDs por ventilador), **si la fila anterior funciona** | LHM / OpenRGB | No se distinguen ventiladores encadenados en un mismo cabezal de ventilador. En el RGB sí, por posición del LED |
| **Bomba Nautilus 360 RS** | ✅ RPM (en PUMP_FAN1) | ⚠️ PWM de 4 pines. La app aplica límites duros: **nunca 0 %, mínimo 60 %, 100 % por defecto** | ✅ LEDs del cabezal, encadenados con los ventiladores del radiador, si la fila de Mystic Light funciona | LHM / OpenRGB | Corsair no publica el rango de RPM de la bomba; lo mediremos (solo entre el 100 % y el 60 %) |
| **Caja FRAME 4000D RS ARGB** | — | (sus 3 ventiladores están en la fila de RS120) | (ídem) | — | No trae hub ni controladora: toda su iluminación son los ventiladores |
| **RAM Vengeance RGB DDR5 ×2** | ⚠️ temperatura del módulo (sensor del SPD de DDR5; LHM 0.9.6 incluye RAMSPDToolkit) | — | ⚠️ OpenRGB tiene "Corsair Vengeance RGB DDR5" (10 LEDs por módulo) por SMBus. Tu número de pieza no aparece en el código: se identifica por un ID interno que leeremos en la Fase 0 | OpenRGB + PawnIO | iCUE no puede estar abierto a la vez: los dos escriben en el mismo bus |
| **Hubs Corsair, Lian Li, NZXT** | — | — | — | — | No tienes ninguno, así que no aplica |

### Cómo va conectado (según la guía oficial de Corsair y las fichas técnicas)
- **Bomba**: cable PWM de 4 pines al cabezal de bomba de la placa (**PUMP_FAN1**). Consume como máximo 12 V y 1 A.
- **Ventiladores del radiador**: van en cadena, y su PWM va con un alargador al **CPU_FAN1** (0,4 A cada uno, 1,2 A en total). Por eso, en tu PC, "el ventilador de la CPU" es el radiador.
- **ARGB de la líquida**: bomba → ventilador 1 → 2 → 3 → un cabezal ARGB de 5 V de la placa.
- **6 ventiladores de la caja**: van en cadena a uno o varios SYS_FAN (PWM) y a un cabezal ARGB.
- ⚠️ **Corriente**: si los 6 ventiladores de la caja están en **un solo** SYS_FAN, suman hasta 6 × 0,4 A = 2,4 A. MSI suele dar 1 A por SYS_FAN. **Compruébalo en el manual**; no he podido leer la web de MSI porque bloquea el acceso automático. Tienes 6 SYS_FAN: repartir los ventiladores es más seguro y además da lectura y control individuales.

---

## 2. Lo que no se puede (o no está garantizado) y alternativas reales

1. **RGB de la placa (Mystic Light 0DB0:0076).** Si en la Fase 0 OpenRGB 1.1 no lo detecta o no controla bien los cabezales ARGB:
   - a) Probar la versión de desarrollo de OpenRGB (las builds "pipeline" que publican en su web) y, si tampoco funciona, abrir un aviso "[New Device]" en el GitLab de OpenRGB con los datos de tu placa.
   - b) Hardware: un controlador ARGB por USB que aparezca en la **lista oficial** de OpenRGB, o un ESP32 con WLED (OpenRGB lo controla por red mediante E1.31). Los ventiladores se conectarían ahí en vez de a la placa.
   - c) Último recurso: dejar los ventiladores con el efecto que guarde MSI Center y que la app controle solo la RAM.
   - **No propongo** escribir yo el protocolo USB de MSI: no hay documentación oficial, y el propio OpenRGB advierte del "riesgo de brickeo" en controladores Mystic Light no probados. Tampoco el SDK oficial de MSI: `MysticLight_SDK.dll` no está en tu instalación de MSI Center.
2. **RGB de la GPU.** Las PRIME RTX 5070 no anuncian iluminación. Si la tuya tiene, OpenRGB no reconoce el subsistema 89E6. La alternativa sería el software de ASUS, o pedir el soporte en OpenRGB.
3. **RPM por ventilador individual**: imposible si están en cadena, porque es un límite físico. Alternativa opcional: un ventilador por SYS_FAN con alargadores RS. Tienes 6 SYS_FAN para 6 ventiladores de caja.
4. **Cliente C# para OpenRGB.** OpenRGB.NET (v3.1.1, sin cambios desde 07/2024) solo implementa el **protocolo 4**, y OpenRGB 1.x usa el **protocolo 6**. Escribiré **un cliente propio** siguiendo `Documentation/OpenRGBSDK.md` oficial. Negociará la versión con el servidor, que responde con la menor de las dos.
5. **NvAPIWrapper** no se actualiza desde 2021. Usaré la implementación de NvAPI de LibreHardwareMonitor, que se mantiene y tiene código específico para las RTX 50. **NVML** queda como plan B si el control falla en tu tarjeta o desde el servicio.
6. **.NET 8** está **sin soporte desde el 10-11-2026**. Usaré **.NET 10 LTS**, con soporte hasta el 14-11-2028.

---

## 3. Tecnología final (y por qué cambia)

| Pieza | Elección | Motivo |
|---|---|---|
| Lenguaje | C# / **.NET 10** (SDK 10.0.404) | .NET 8 ya está fuera de soporte |
| Interfaz | **WPF + WPF-UI 4.3** (Fluent, Mica) con plantillas propias | Bandeja del sistema, ventana sin bordes personalizada y LiveCharts2 maduro; se empaqueta en un .exe sin MSIX. WinUI 3 complica la bandeja y el empaquetado |
| Gráficas | **LiveCharts2** 2.x estable (SkiaSharp) | 2.0 ya es estable |
| Sensores y ventiladores (placa y GPU) | **LibreHardwareMonitorLib 0.9.6** + driver **PawnIO 2.2.0** | La versión actual, que usa PawnIO en lugar de WinRing0 |
| RGB | **OpenRGB 1.1.0** (como servicio de fondo, SDK en el puerto 6742) + **cliente propio** | No hay ningún cliente C# compatible con el protocolo 6 |
| Núcleo | **Servicio de Windows** (cuenta LocalSystem) | Arranca con Windows **sin aviso de UAC** y sigue funcionando sin la ventana |
| Interfaz al iniciar sesión | Proceso **sin privilegios** (entrada de inicio de usuario) | Como el hardware lo toca el servicio, la interfaz **no necesita ser administrador**. Ni UAC ni una tarea programada elevada: es más seguro y cumple lo mismo |
| Comunicación | **Named pipe** `\\.\pipe\cefiro` con mensajes JSON, que solo acepta tu usuario y los administradores | |
| Audio (efecto música) | NAudio (captura del audio del sistema por WASAPI) en la interfaz, que envía el espectro al servicio | Un servicio no puede capturar bien el audio de tu sesión |
| Registros | Serilog → `%ProgramData%\Cefiro\logs` | |
| Tests | xUnit | |
| Instalador | **Inno Setup 6.7.3** + publicación autocontenida | |

Todo lo que hay que instalar está en winget: `Microsoft.DotNet.SDK.10`, `JRSoftware.InnoSetup`, `namazso.PawnIO` y `OpenRGB.OpenRGB`.

---

## 4. Arquitectura

```
┌──────────────── Tu sesión de Windows (sin admin) ────────────────┐
│  Cefiro.App (WPF)                                                │
│  ventana · bandeja · atajos de teclado · notificaciones          │
│  captura de audio → espectro para el efecto música               │
└───────────────▲──────────────────────────────────────────────────┘
                │  named pipe (JSON) — solo tu usuario y los admins
┌───────────────▼───────── Servicio de Windows (LocalSystem) ──────┐
│  Cefiro.Service                                                   │
│  SensorHub (LHM, 1 Hz) → historial de 60 s / 10 min / 1 h         │
│  FanEngine (curvas, histéresis, suavizado) ─► SafetyGuard ─► HW   │
│  LightingEngine (efectos) ─► cliente OpenRGB                      │
│  Perfiles · automatizaciones (.exe) · alertas · detector de       │
│  conflictos · registro                                            │
└──────┬─────────────────────────────────┬──────────────────────────┘
       │ LibreHardwareMonitorLib + PawnIO │ TCP 127.0.0.1:6742
       ▼                                  ▼
  NCT6687D (8 cabezales) · CPU · GPU   OpenRGB 1.1 (servicio)
                                       → Mystic Light USB · RAM por SMBus
```

### Seguridad del hardware (en `SafetyGuard`, con tests)
- **Límites duros** que no se pueden configurar por debajo:
  - bomba ≥ 60 % (100 % por defecto, nunca 0);
  - CPU_FAN1 (radiador) ≥ 30 % y nunca 0.
- Antes de enviar cualquier valor se descartan NaN e infinitos, se limita a 0–100 % y se aplican los mínimos. Solo se escribe si el valor cambia.
- **Modo de emergencia**: si la CPU o la GPU llegan a la temperatura crítica (configurable), todos los ventiladores (también los de la GPU) van al 100 %. Se sale cuando la temperatura baja X °C durante 15 s.
- **Sensor caído** (sin lectura válida durante más de 3 s): los cabezales que dependen de él vuelven a la BIOS.
- **Cierre ordenado o excepción no controlada**: todos los cabezales vuelven a la BIOS.
- **Muerte brusca del proceso** (por ejemplo, si lo matas desde el Administrador de tareas): el chip conserva el último valor. Windows reinicia el servicio en unos 2 s y lo primero que hace es devolver el control a la BIOS. Durante esos segundos los ventiladores se quedan como estaban, y no puede ser de otra forma porque el chip no tiene un watchdog propio.
- **Al arrancar**, la app no toca nada hasta tener lecturas válidas. Hasta entonces manda la BIOS.

---

## 5. Estructura de carpetas

```
Aplicacion luces/
├─ src/
│  ├─ Cefiro.Core/        lógica pura: curvas, histéresis, suavizado, seguridad, perfiles, modelos
│  ├─ Cefiro.Hardware/    LHM (sensores y ventiladores de placa y GPU), comprobación de PawnIO, conflictos
│  ├─ Cefiro.OpenRgb/     cliente propio del SDK de OpenRGB (protocolo 4–6)
│  ├─ Cefiro.Lighting/    motor de efectos RGB
│  ├─ Cefiro.Ipc/         contratos y transporte por named pipe
│  ├─ Cefiro.Service/     servicio de Windows (el núcleo)
│  ├─ Cefiro.App/         interfaz WPF + bandeja
│  └─ Cefiro.Demo/        hardware simulado para --demo (separado del resto)
├─ tests/
│  ├─ Cefiro.Core.Tests/      curvas, histéresis, emergencia, validación
│  └─ Cefiro.OpenRgb.Tests/   serialización de paquetes del protocolo
├─ tools/HwProbe/         diagnóstico de tu hardware (Fase 0)
├─ installer/             script de Inno Setup
├─ assets/                icono, pantalla de carga, fuentes
├─ docs/                  PLAN.md, COMPATIBILIDAD.md, PRUEBAS.md
└─ README.md              en español
```

---

## 6. Fases (commit de git al final de cada una)

| Fase | Contenido | Cómo se comprueba |
|---|---|---|
| **0. Preparación y prueba real** | Instalar con winget .NET 10 SDK, Inno Setup, PawnIO y OpenRGB 1.1. Desactivar temporalmente Mystic Light y LEDKeeper2 de MSI Center, iCUE y la Iluminación dinámica de Windows. `git init` y esqueleto. Herramienta **HwProbe**, que ejecutarás como admin (aceptando el UAC) y que vuelca: chip, sensores, cabezales con control, GPU (también como SYSTEM) y los dispositivos de OpenRGB (placa, RAM, GPU) | Tabla de compatibilidad **definitiva** y decisión sobre el RGB de la placa. Si algo crítico falla, paro y te lo cuento |
| **1. Sensores** | Core, Hardware, Servicio, IPC, historial, registro, detector de conflictos, modo demo | Un cliente de consola muestra datos en vivo. CPU del servicio < 1 % |
| **2. Ventiladores** | Curvas, histéresis, suavizado, % mínimo y de arranque, fuentes de temperatura (CPU, GPU, placa, máximo, media), grupos, modos predefinidos, calibración, SafetyGuard, emergencia y vuelta a la BIOS | Tests unitarios. Pruebas contigo, por ejemplo: "SYS_FAN1 al 30 %, ¿bajan las RPM?" |
| **3. RGB** | Cliente SDK propio, dispositivos, zonas y LEDs, efectos (estático, respiración, arcoíris, ola, ciclo, cometa, temperatura, música y los modos nativos), brillo, velocidad, sincronizar todo, modo noche | Tests de los paquetes. Prueba real zona a zona y LED a LED |
| **4. Interfaz** | Tema propio (cristal, neón, acento configurable o que siga a las luces), barra lateral, Panel (indicadores circulares, gráficas, **esquema animado de tu caja**), Ventiladores (tarjetas y editor de curvas), Iluminación (vista de LEDs, rueda de color, HEX, favoritos), bandeja, pantalla de carga e icono | Revisión visual con capturas, en modo demo y con datos reales |
| **5. Perfiles y extras** | Perfiles (ventiladores y RGB), cambio desde la bandeja y con atajos, cambio automático por .exe, exportar/importar JSON, notificaciones, ajustes (inicio con Windows, °C/°F, español), avisos de conflictos | Pruebas funcionales |
| **6. Entrega** | Medición de consumo, README en español, instalador y .exe autocontenido, lista de pruebas para tu PC | Instalación limpia y lista de pruebas. Etiqueta v1.0 |

---

## 7. Lo que necesito de ti
1. **OK al plan.**
2. Permiso para instalar con winget: **.NET 10 SDK, Inno Setup, PawnIO (driver) y OpenRGB 1.1**, y para desactivar temporalmente los servicios de Mystic Light (MSI) e iCUE. Desinstalar no hace falta todavía.
3. En la Fase 0: aceptar el UAC para ejecutar HwProbe como administrador, y apagar tú la **Iluminación dinámica** de Windows (Configuración → Personalización → Iluminación dinámica).
4. Datos físicos, que se pueden deducir en la Fase 2 con pruebas guiadas:
   - si el radiador está en el techo o en el frontal;
   - qué ventiladores van a cada SYS_FAN;
   - si tienes montados los 9 ventiladores.
5. El **nombre** de la app.

## 8. Tres nombres propuestos
1. **Fulgor**: "brillo intenso". Corto, en español y con sonido de producto premium.
2. **Céfiro** ✅ (elegido): el viento suave del oeste. Une flujo de aire y elegancia.
3. **Nimbo**: el halo de luz. Luz y aire en una palabra.

---

## Fuentes consultadas
- OpenRGB, código y documentación: [repositorio en GitLab](https://gitlab.com/CalcProgrammer1/OpenRGB) (`Controllers/MSIMotherboardController`, `Controllers/CorsairDRAMController`, `Controllers/AsusAuraGPUController`, `pci_ids/pci_ids.h`, `NetworkProtocol.h`, `Documentation/OpenRGBSDK.md`, `Documentation/SMBusAccess.md`), [versiones](https://openrgb.org/releases.html), [aviso de la B650 Gaming Plus WiFi](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/3578), [novedades de la 1.0](https://linuxiac.com/openrgb-1-0-released-after-nearly-three-years-of-development/)
- LibreHardwareMonitor: [versiones](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases), [NuGet](https://www.nuget.org/packages/LibreHardwareMonitorLib), [PR #1857 (PawnIO)](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857)
- OpenRGB.NET: [GitHub](https://github.com/diogotr7/OpenRGB.NET), [NuGet](https://www.nuget.org/packages/OpenRGB.NET)
- PawnIO: [versiones](https://github.com/namazso/PawnIO.Setup/releases), [pawnio.eu](https://pawnio.eu/)
- .NET: [política de soporte](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- WPF-UI: [NuGet](https://www.nuget.org/packages/WPF-UI) · LiveCharts2: [NuGet](https://www.nuget.org/packages/LiveChartsCore.SkiaSharpView.WPF)
- Corsair: [guía de inicio de la Nautilus 360 RS ARGB](https://www.corsair.com/explorer/diy-builder/cpu-coolers/nautilus-360-rs-argb/) · fichas de PcComponentes: [RS120 ARGB](https://www.pccomponentes.com/corsair-rs120-argb-pack-triple-ventilador-suplementario-pwm-120mm-blanco), [Nautilus 360 RS ARGB](https://www.pccomponentes.com/corsair-nautilus-360-rs-argb-kit-refrigeracion-liquida-360mm-blanco), [FRAME 4000D RS ARGB](https://www.pccomponentes.com/torre-pc-corsair-frame-4000d-rs-argb-semi-torre-e-atx-cristal-templado-usb-c-blanca)
- MSI: [especificaciones de la X670E GAMING PLUS WIFI](https://www.msi.com/Motherboard/X670E-GAMING-PLUS-WIFI/Specification) (1 CPU_FAN, 1 PUMP_FAN y 6 SYS_FAN)
- ASUS: [PRIME RTX 5070 OC](https://www.asus.com/motherboards-components/graphics-cards/prime/prime-rtx5070-o12g/techspec/)
