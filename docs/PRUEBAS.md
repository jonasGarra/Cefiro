# Lista de pruebas de Céfiro en tu PC

Marca cada casilla al comprobarla. Entre paréntesis, lo que deberías ver. Las pruebas automáticas
(`dotnet test --solution Cefiro.slnx`, 106 pruebas) ya cubren curvas, histéresis, emergencia, protocolo de
OpenRGB, efectos y perfiles; esta lista es lo que solo se puede comprobar con tu hardware delante.

> Ya comprobado durante el desarrollo (22-11-2026): instalación del servicio, lectura de los 113 sensores,
> control de cada conector, protección de la bomba, emergencia, vuelta a la BIOS al detener o matar el
> servicio, calibración, todas las zonas RGB y el efecto Música, y el consumo de CPU.

## 0. Antes de empezar

- [ ] **PawnIO** instalado (*Ajustes › Servicio y hardware*: «PawnIO instalado · el servicio tiene permisos de administrador»).
- [ ] **OpenRGB** en marcha con su servidor (*Iluminación*: «3 dispositivos · OpenRGB 1.1 · protocolo 6»).
- [ ] **Nada más mandando** (*Ajustes › Programas que pueden interferir*: «No se ha encontrado ninguno abierto»).
      Desinstala el módulo Mystic Light de MSI Center y apaga la iluminación dinámica de Windows.
- [ ] **BIOS**: CPU_FAN1 (la bomba) con una curva que nunca baje del 60 % (o al 100 % fijo). Ver el README.

## 1. Instalación e inicio

- [ ] El instalador muestra la página de requisitos con PawnIO y OpenRGB encontrados.
- [ ] `services.msc` → «Céfiro»: *En ejecución*, inicio *Automático*; pestaña *Recuperación*: «Reiniciar el servicio» las tres veces.
- [ ] La app se abre **sin aviso de UAC** y aparece el icono en la bandeja.
- [ ] Reinicia el PC: el servicio arranca solo y la app aparece en la bandeja (si marcaste la opción), sin UAC.
- [ ] Abrir Céfiro otra vez desde el menú Inicio no crea otra ventana: muestra la que ya hay.

## 2. Panel y sensores

- [ ] Temperatura de CPU y GPU, uso, frecuencia y consumo parecidos a los de HWiNFO (±1 °C).
- [ ] Bomba sobre 2300–2700 RPM; radiador y caja entre 700 y 1500 RPM en modo BIOS.
- [ ] Historial: 60 s, 10 min y 1 h se rellenan (1 h necesita que el servicio lleve un rato en marcha).
- [ ] *Tu PC en directo*: los ventiladores giran más rápido cuando suben sus RPM y los LEDs tienen el color real.
- [ ] *Sensores*: 113 sensores; el buscador filtra (prueba «VRM»).

## 3. Ventiladores

- [ ] **Equilibrado**: las tarjetas pasan a *Curva* y las RPM cambian suavemente (sin saltos).
- [ ] **Silencioso**, **Rendimiento**, **Máximo** y **Todo en BIOS** se marcan y hacen lo que dicen.
- [ ] **Bomba en Fijo**: el deslizador no baja de la zona roja (60 %) y las RPM de la bomba nunca bajan de ~2200.
- [ ] **Radiador en Fijo**: no baja del 30 %.
- [ ] **Caja**: en sus ajustes, *Permitir que se pare (0 RPM)* y *Fijo* al 0 % → se para. Al subirlo, arranca (empujón de arranque).
- [ ] **Editor de curva**: arrastrar puntos, doble clic añade, clic derecho quita; la línea de temperatura se mueve en directo.
- [ ] Caja con fuente *La más alta de CPU y gráfica*: jugando, la caja sigue a la GPU cuando esta está más caliente.
- [ ] **Grupos**: mete radiador y caja en un grupo y cambia la curva de uno → cambia en los dos (la bomba conserva su mínimo).
- [ ] **Calibración** de la caja (botón en sus ajustes, ~2 min) → *Aplicar mínimos calibrados*.
- [ ] Cambia el nombre de un ventilador, reinicia el PC → el nombre sigue.

## 4. Seguridad (importante)

- [ ] **Emergencia sin riesgo**: en *Ventiladores › Seguridad* baja *Emergencia de la CPU* a 60 °C y lanza una carga
      (Cinebench, 1–2 min) → banner rojo, todo al 100 % y notificación. Al parar la carga, cuando la CPU lleva 15 s
      8 °C por debajo, se sale sola. Después pulsa **Restaurar valores seguros** (vuelve a 90 °C).
- [ ] **Detener el servicio** (*services.msc › Detener*) con Equilibrado activo → en ~1 s los ventiladores siguen la
      curva de la BIOS y la app muestra «Conectando con el servicio…». *Iniciar* → vuelve Equilibrado.
- [ ] **Matar el servicio** (Administrador de tareas como administrador › *Detalles* › `Cefiro.Service.exe` › *Finalizar tarea*)
      → Windows lo vuelve a arrancar en ~5 s; al arrancar devuelve todo a la BIOS y aplica tu modo otra vez.
- [ ] Ningún perfil ni ajuste consigue parar la bomba (prueba a importar un perfil con la bomba al 0 %: se queda en el 60 %).

## 5. Iluminación

- [ ] **Estático** cian: placa, líquida, ventiladores de la caja y las dos RAM en cian.
- [ ] Cada efecto: Respiración, Arcoíris, Ola, Ciclo de color, Cometa, **Temperatura** (con carga pasa de cian a ámbar y rosa),
      **Música** (con música reacciona; sin sonido queda un brillo tenue) y **Modo del dispositivo** (p. ej. «Rainbow wave»).
- [ ] *Sincronizar todo* desactivado: RAM 1 en Música y JRAINBOW1 en Arcoíris → solo cambian esos.
- [ ] Pulsa un LED de JRAINBOW1 y ponlo rojo → ese LED cambia en los ventiladores de la caja (van en paralelo).
- [ ] El ojo de un dispositivo o zona lo hace parpadear en blanco unos segundos.
- [ ] **Brillo general** al 20 % → baja todo; **Velocidad general** acelera o frena los efectos.
- [ ] **Modo noche**: pon *Desde* un minuto después de la hora actual → a esa hora atenúa (o apaga); al llegar *Hasta*, vuelve.
- [ ] Favoritos: guardar el color actual (estrella), aplicarlo y quitarlo con clic derecho.
- [ ] Reinicia el servicio de OpenRGB → en unos segundos Céfiro se reconecta y vuelve a poner tu efecto.
- [ ] Icono de la bandeja › *Apagar las luces* / *Encender las luces*.

## 6. Perfiles

- [ ] Crea «Juegos» (Rendimiento + Arcoíris) y «Silencio» (Silencioso + Estático tenue) con *Nuevo perfil con lo actual*.
- [ ] Actívalos desde la app, desde la bandeja (*Perfiles*) y con un atajo (p. ej. Ctrl+Alt+1) **con la ventana cerrada**.
- [ ] Asocia `notepad.exe` a «Juegos»: al abrir el Bloc de notas cambia en ≤ 3 s (con notificación); al cerrarlo vuelve lo que había.
- [ ] Con el Bloc de notas abierto, activa «Silencio» a mano → se respeta hasta que lo cierres.
- [ ] Exporta un perfil a un archivo e impórtalo.

## 7. Avisos y ajustes

- [ ] *Ajustes › Avisos › Probar una notificación*.
- [ ] Baja *Aviso de CPU caliente* a 55 °C y lanza una carga → una sola notificación (no se repite en 10 min). Vuelve a dejarlo en 85 °C.
- [ ] °C / °F cambia todas las temperaturas, también en las curvas.
- [ ] Color de acento y *Que el acento siga a las luces del PC* (con Arcoíris va cambiando poco a poco).
- [ ] Cerrar la ventana la deja en la bandeja (la primera vez avisa); *Salir* desde la bandeja la cierra del todo
      (el servicio sigue controlando los ventiladores y las luces).

## 8. Consumo

- [ ] Administrador de tareas: `Cefiro.Service.exe` por debajo del 1 % en reposo; `Cefiro.exe` en la bandeja ~0 %.
      (Medido: servicio 0,12–0,17 %, app en la bandeja 0,04 %, ventana abierta en el Panel ~0,9 %.)

## 9. Desinstalación (opcional)

- [ ] *Configuración › Aplicaciones › Céfiro › Desinstalar* → el servicio desaparece de `services.msc`, los ventiladores
      vuelven a la BIOS y la configuración queda en `%ProgramData%\Cefiro`.
