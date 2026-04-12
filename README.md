# CMBuyerStudio

> Tu asistente para preparar compras de **Magic: The Gathering** en **Cardmarket**.

<p align="center">
  <strong>Busca mejor.</strong> • <strong>Guarda tu lista.</strong> • <strong>Optimiza vendedores.</strong>
</p>

---

## 📚 Índice

- [✨ ¿Qué es CMBuyerStudio?](#-qué-es-cmbuyerstudio)
- [🚀 Qué puedes hacer hoy](#-qué-puedes-hacer-hoy)
- [🧭 Flujo recomendado (rápido)](#-flujo-recomendado-rápido)
- [🖥️ Pantallas principales](#️-pantallas-principales)
- [📄 Informes que genera](#-informes-que-genera)
- [💾 Tus datos y privacidad](#-tus-datos-y-privacidad)
- [📋 Requisitos](#-requisitos)
- [▶️ Cómo empezar](#️-cómo-empezar)
- [⚠️ Límites actuales](#️-límites-actuales)
- [🛣️ Estado actual del proyecto](#️-estado-actual-del-proyecto)

---

## ✨ ¿Qué es CMBuyerStudio?

**CMBuyerStudio** es una app de escritorio para Windows pensada para jugadores y coleccionistas que compran cartas sueltas en Cardmarket.

Te ayuda a pasar de “tengo que mirar muchas opciones” a un flujo claro:

1. Buscar cartas.
2. Guardar solo las variantes que te interesan.
3. Calcular combinaciones de vendedores para comprar mejor.

---

## 🚀 Qué puedes hacer hoy

- 🔎 Buscar cartas en Cardmarket y ver variantes con precio e imagen.
- 🎯 Seleccionar solo las versiones que aceptarías comprar.
- 📦 Guardar cantidad deseada por carta en tu lista.
- 🧾 Gestionar la lista: cambiar cantidades, quitar variantes, borrar grupos o limpiar todo.
- ▶️ Ejecutar el cálculo de “best seller” con progreso en tiempo real.
- 🌍 Obtener resultados en dos ámbitos:
  - **EU** (vendedores europeos)
  - **Local** (vendedores de España)
- 📄 Abrir informes HTML generados con desglose de vendedores, precios y cobertura.
- ⚙️ Configurar filtros y preferencias de scraping desde la app.

---

## 🧭 Flujo recomendado (rápido)

1. Ve a **Search** y busca la carta.
2. Marca variantes y define cantidad.
3. Pulsa **Save Selection**.
4. Revisa en **Wanted Cards**.
5. Ajusta **Settings** (países, idioma, condición mínima, envío, etc.) si lo necesitas.
6. Ejecuta **Run Best Seller**.
7. Abre los informes **EU** y **Local**.

---

## 🖥️ Pantallas principales

### 🔎 Search

- Búsqueda por nombre.
- Resultados ordenados por precio.
- Selección múltiple de variantes.
- Guardado de selección con cantidad deseada.

Nota: si guardas de nuevo una carta con el mismo nombre, se actualiza con la nueva selección guardada.

### 🃏 Wanted Cards

- Vista de todas tus cartas guardadas.
- Edición directa de cantidades.
- Eliminación de variantes sueltas.
- Eliminación de grupos completos.
- Botón para vaciar toda la lista.

### ▶️ Run Best Seller

- Ejecuta scraping + optimización.
- Barra de progreso y estado por fases.
- Cancelación manual de ejecución.
- Totales separados para **EU** y **Local**.
- Botones para abrir los informes generados al terminar.

### ⚙️ Settings

Desde aquí puedes ajustar:

- Caducidad de caché.
- Coste de envío por defecto y por país.
- Usuario y contraseña de Cardmarket.
- Condición mínima de carta.
- Países de vendedor permitidos.
- Idiomas permitidos.
- Proxies (opcionales).

### 📄 Logs

Pantalla visible en la navegación, todavía en estado básico.

---

## 📄 Informes que genera

Al finalizar una ejecución, la app puede generar:

- **Informe EU**
- **Informe Local**

Cada informe incluye resumen de coste total, vendedores seleccionados, cartas cubiertas/no cubiertas y enlaces directos para revisar opciones en Cardmarket.

---

## 💾 Tus datos y privacidad

La app guarda la información de uso en tu equipo, dentro de:

```text
%LOCALAPPDATA%\CMBuyerStudio
```

Ahí se conserva tu lista, la caché de búsqueda/imágenes, los informes y datos de ejecución.

---

## 📋 Requisitos

- Windows
- Conexión a internet
- Acceso a Cardmarket

Para usar bien **Run Best Seller**, es recomendable tener configurada tu cuenta de Cardmarket en **Settings**.

---

## ▶️ Cómo empezar

### Opción recomendada (usuario final)

1. Descarga la última versión desde **GitHub Releases**.
2. Abre la app.
3. Empieza en **Search** y sigue el flujo.

### Si todavía no hay release publicada

La app puede requerir ejecución desde código fuente (proceso más técnico).

---

## ⚠️ Límites actuales

- Enfocada en cartas de **Magic: The Gathering** dentro de Cardmarket.
- El resultado depende de la disponibilidad real de ofertas en Cardmarket.
- Si Cardmarket cambia su web, algunas partes del scraping pueden verse afectadas.
- La sección **Logs** aún no está al nivel del resto de pantallas.

---

## 🛣️ Estado actual del proyecto

### ✅ Ya funcional para uso real

- Search
- Wanted Cards
- Run Best Seller (con informes EU/Local)
- Settings

### 🚧 A mejorar

- Logs
- Más pulido general de experiencia y seguimiento de ejecuciones

---

Si quieres preparar compras de forma más ordenada, comparar mejor opciones y guardar una lista clara antes de pagar, **CMBuyerStudio ya te aporta valor desde hoy**.
