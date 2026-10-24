# Tiempo & Estilo — Tienda de relojes con microservicios

Proyecto académico basado en el **Tema 6: Sistema de Microservicios con Kafka para el Procesamiento de Datos en Tiempo Real**. Contiene una tienda web responsive, catálogo de relojes, carrito, checkout, gestión básica de inventario, pedidos y cobros **simulados**, tres APIs ASP.NET Core 8, Kafka, SQLite y Docker Compose.

## Requisitos
- Docker Engine + Docker Compose v2 (o Docker Desktop).
- Conexión a Internet la primera vez para descargar imágenes y paquetes NuGet.
- Puertos **8088** disponible.

## Inicio rápido

```bash
docker compose up --build -d
docker compose ps
```

Abrir **http://localhost:8088**. El arranque inicial puede tardar mientras Kafka y las APIs se preparan. Consultar logs con `docker compose logs -f`.

## Demostración paso a paso
1. Abrir el catálogo y seleccionar un reloj.
2. Pulsar «Añadir a la cesta». El reloj aparece en el panel «Tu cesta», a la derecha del catálogo.
3. Completar nombre, teléfono, dirección y método de pago.
4. Crear pedido. `OrdersApi` publica `OrderPlaced` en Kafka.
5. `InventoryApi` consume el evento, comprueba y reserva existencias; publica `StockReserved` o `StockRejected`.
6. `OrdersApi` consume el resultado y cambia el estado del pedido. `PaymentsApi` recibe `StockReserved` y crea el pago pendiente.
7. Cuando aparezca «Pendiente de pago», pulsar «Simular pago aprobado» o «Simular pago rechazado».
8. `PaymentsApi` publica `PaymentConfirmed` o `PaymentFailed`; `InventoryApi` confirma la salida o libera la reserva, y `OrdersApi` actualiza el pedido.
9. Abrir «Administración» para ver existencias, pedidos, movimientos y registrar relojes.

## Servicios
| Servicio | Uso interno | Base de datos |
|---|---|---|
| InventoryApi | productos, ajustes, reservas, movimientos | inventory.db |
| OrdersApi | pedidos, clientes y estados | orders.db |
| PaymentsApi | pagos de demostración | payments.db |
| Kafka | bus de eventos `shop-events` | Kafka |
| Nginx | frontend y proxy `/api/*` | no aplica |

## Endpoints
- `GET /api/inventory/products`
- `POST /api/inventory/products`
- `PUT /api/inventory/products/{id}`
- `DELETE /api/inventory/products/{id}`
- `POST /api/inventory/products/{id}/adjust` `{ "change": 2, "reason": "Ingreso" }`
- `GET /api/inventory/movements`
- `POST /api/orders/orders`
- `GET /api/orders/orders/{guid}`
- `GET /api/orders/orders`
- `POST /api/orders/orders/{guid}/cancel`
- `GET /api/payments/payments`
- `GET /api/payments/payments/{guid}`
- `POST /api/payments/payments/{guid}/simulate` `{ "result": "approved" }` o `rejected`

Ejemplo de creación de pedido:
```bash
curl -X POST http://localhost:8088/api/orders/orders \
  -H 'Content-Type: application/json' \
  -d '{"customer":"Cliente Demo","phone":"70000000","address":"San Salvador","paymentMethod":"transferencia","items":[{"productId":1,"quantity":1}]}'
```

## Estructura
- `frontend/`: HTML, CSS, JavaScript e ilustraciones SVG propias.
- `InventoryApi/`: inventario, reservas y consumidor Kafka.
- `OrdersApi/`: pedidos y consumidor Kafka.
- `PaymentsApi/`: cobros simulados y consumidor Kafka.
- `Shared/`: contratos y publicador/consumidor Kafka reutilizables.
- `docs/`: enfoque y decisiones, arquitectura, manual de usuario, informe académico y pruebas. La versión para entregar está en `docs/ENFOQUE.pdf`.
- `compose.yaml`: contenedores, volúmenes y dependencias.

## Persistencia
Cada API almacena su SQLite en un volumen Docker. Para reiniciar datos **y borrar pedidos**: `docker compose down -v`. Para detener sin borrar: `docker compose down`.

## Avisos importantes
**Solo para demostración académica/local.** No tiene autenticación, autorización, validación de identidad, pasarela bancaria ni controles de producción. El panel administrativo y los endpoints de escritura están abiertos. No publicar en Internet ni introducir datos personales reales. No se realizan cargos reales. Los mensajes Kafka se procesan de forma asíncrona y pueden tardar unos segundos.

**Limitación de consistencia:** el proyecto es un prototipo educativo; no implementa outbox transaccional, reconciliación automática, expiración de reservas, ni coordinación robusta ante fallas entre guardar en SQLite y publicar en Kafka. Para producción se requieren estos mecanismos, además de seguridad, auditoría, idempotencia de solicitudes HTTP y pruebas de concurrencia.

## Validación
`dotnet build` compila Shared, InventoryApi, OrdersApi y PaymentsApi. `docker compose up --build -d` publica la tienda en el puerto 8088. Al crear un pedido, OrdersApi publica `OrderPlaced`; InventoryApi reserva existencias y el estado pasa a `awaiting-payment`; al simular el pago aprobado el pedido queda `paid` y el stock baja una unidad. El plan de casos está en `docs/PRUEBAS.md`.
