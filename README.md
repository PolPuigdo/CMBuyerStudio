# CMBuyerStudio

Aplicacion de escritorio para Windows orientada a buscar cartas de **Magic: The Gathering** en **Cardmarket**, comparar variantes rapidamente y construir una lista de compra guardada para consultarla y mantenerla organizada.

## Que es CMBuyerStudio

CMBuyerStudio esta pensado para quien compra cartas en Cardmarket y quiere:

- buscar una carta y ver sus variantes disponibles;
- revisar rapidamente set, precio e imagen;
- seleccionar solo las versiones que le interesan;
- guardar una lista deseada con cantidades objetivo;
- mantener esa lista persistida entre sesiones.

La aplicacion no intenta ser un visor tecnico del proyecto, sino una herramienta practica para preparar compras de cartas de forma mas comoda.

## Estado actual

El proyecto ya permite usar el flujo principal de trabajo:

- buscar cartas en Cardmarket;
- seleccionar variantes concretas;
- guardar grupos de cartas deseadas;
- editar cantidades;
- eliminar variantes o grupos completos;
- conservar la informacion guardada automaticamente.

Ahora mismo las secciones **Run**, **Settings** y **Logs** aparecen en la interfaz, pero todavia estan en desarrollo y no ofrecen funcionalidad real.

## Que puede hacer hoy

### 1. Buscar cartas en Cardmarket

Desde la pantalla de busqueda puedes escribir el nombre de una carta y lanzar la consulta. La aplicacion obtiene resultados desde Cardmarket para la categoria de **Singles de Magic**.

Cada resultado muestra:

- nombre de la carta;
- expansion o set;
- precio;
- imagen de referencia;
- selector para marcar la variante.

Los resultados se ordenan por precio para facilitar una comparacion rapida.

### 2. Seleccionar variantes concretas

Puedes marcar una o varias versiones de la misma carta. Esto es util cuando:

- te sirven varias expansiones;
- quieres limitarte a ciertos sets;
- quieres preparar una lista flexible segun el precio.

Tambien existe un boton de **Select All** para marcar todos los resultados visibles.

### 3. Guardar una lista deseada

Una vez seleccionadas las variantes, puedes indicar la **cantidad deseada** y guardar la seleccion.

La aplicacion agrupa la carta por nombre y guarda dentro de ese grupo las variantes permitidas. Si vuelves a guardar la misma carta:

- la cantidad deseada se suma;
- las variantes nuevas se anaden;
- las variantes duplicadas no se repiten.

### 4. Gestionar las cartas deseadas

En la pantalla **Wanted Cards** puedes:

- ver todos los grupos guardados;
- cambiar la cantidad deseada de cada carta;
- quitar variantes concretas;
- eliminar un grupo completo;
- vaciar toda la lista.

Los cambios se guardan automaticamente.

## Flujo de uso recomendado

1. Abre la aplicacion.
2. Entra en **Search**.
3. Busca una carta por nombre.
4. Marca las variantes que aceptarias comprar.
5. Indica la cantidad deseada.
6. Pulsa **Save Selection**.
7. Ve a **Wanted Cards** para revisar o ajustar tu lista.

Este es, a dia de hoy, el flujo principal y plenamente util del proyecto.

## Como funciona a nivel de usuario

Cuando haces una busqueda, la aplicacion abre una sesion automatizada del navegador para consultar Cardmarket y extraer los resultados visibles. Despues descarga y guarda en cache las imagenes encontradas para que la experiencia sea mas comoda en usos posteriores.

La lista de cartas deseadas se guarda en tu equipo, por lo que al cerrar y volver a abrir la app no pierdes el trabajo.

## Datos que guarda la aplicacion

CMBuyerStudio crea una carpeta propia dentro de:

`%LOCALAPPDATA%\CMBuyerStudio`

Dentro de esa ruta guarda principalmente:

- `cards.json`: tu lista de cartas deseadas;
- `Cache\CardsImages`: imagenes descargadas de las cartas;
- `Reports`: carpeta reservada para futuras funciones;
- `Logs`: carpeta reservada para futuras funciones.

## Requisitos

Para el usuario final, lo importante es esto:

- **Windows**: la app es una aplicacion de escritorio WPF y esta orientada a este sistema;
- **Conexion a internet**: necesaria para buscar resultados en Cardmarket;
- **Acceso a Cardmarket**: si Cardmarket cambia su estructura o bloquea la consulta, la busqueda podria verse afectada.

## Instalacion y arranque

### Opcion recomendada

Si el repositorio publica una version en **Releases**, lo ideal es descargar esa version y ejecutar la aplicacion directamente.

### Si no hay release publicada

El proyecto sigue en una fase temprana. En ese caso, el uso queda mas orientado a ejecutar la aplicacion desde el propio codigo fuente, algo mas tecnico y menos pensado para usuario final.

## Limitaciones actuales

Conviene tener en cuenta estas limitaciones para usar la app con expectativas realistas:

- solo trabaja con cartas de **Magic** dentro de Cardmarket;
- el foco actual esta en buscar, seleccionar y guardar cartas;
- **Run**, **Settings** y **Logs** aun no estan implementados;
- no realiza la compra automaticamente;
- depende de que Cardmarket siga mostrando los datos con una estructura compatible.

## En resumen

CMBuyerStudio ya es util como herramienta para preparar una compra:

- buscas una carta;
- eliges que variantes te interesan;
- guardas cantidad y sets aceptados;
- mantienes tu lista organizada entre sesiones.

Si tu objetivo es construir una lista de compra de Magic de forma visual y comoda antes de decidir que comprar en Cardmarket, esa parte del proyecto ya aporta valor.
