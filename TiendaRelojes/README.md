# Tiempo & Estilo — Tienda de relojes con microservicios

Proyecto académico basado en el **Tema 6: Sistema de Microservicios con Kafka para el Procesamiento de Datos en Tiempo Real**. Contiene una tienda web responsive, catálogo de relojes, carrito, checkout, gestión básica de inventario, pedidos y cobros **simulados**, tres APIs ASP.NET Core 8, Kafka, PostgreSQL en Supabase y Docker Compose.

## Requisitos
- Docker Engine + Docker Compose v2 (o Docker Desktop).
- Conexión a Internet la primera vez para descargar imágenes y paquetes NuGet.
- Puertos **8088** disponible.

## Inicio rápido

1. En Supabase, abre **Connect** y copia la cadena directa de Postgres (puerto **5432**, no el 6543).
2. Copia `.env.example` a `.env` y pega esa cadena en `SUPABASE_DB`, con el formato de claves que muestra el ejemplo.
3. Levanta la tienda:

```bash
docker compose up --build -d
docker compose ps
```

Abrir **http://localhost:8088**. El arranque inicial puede tardar mientras Kafka y las APIs se preparan. Consultar logs con `docker compose logs -f`.

## Demostración paso a paso
1. Abrir el catálogo y seleccionar un reloj.
2. Pulsar «Añadir a la cesta». El reloj aparece en el panel «Tu cesta», a la derecha del catálogo. Ajustar cantidades con `−`/`+`.
3. Pulsar «Iniciar sesión» y crear una cuenta de cliente (o entrar con una existente).
4. Completar nombre, teléfono, dirección y método de pago (tarjeta, transferencia o contra entrega).
5. Crear pedido. `OrdersApi` publica `OrderPlaced` en Kafka.
6. `InventoryApi` consume el evento, comprueba y reserva existencias; publica `StockReserved` o `StockRejected`.
7. `OrdersApi` consume el resultado y cambia el estado del pedido. `PaymentsApi` recibe `StockReserved` y crea el pago pendiente.
8. Cuando aparezca «Pendiente de pago», pulsar «Simular pago aprobado» o «Simular pago rechazado».
9. `PaymentsApi` publica `PaymentConfirmed` o `PaymentFailed`; `InventoryApi` confirma la salida o libera la reserva, y `OrdersApi` actualiza el pedido.
10. «Mis pedidos» muestra los pedidos del cliente y su estado.
11. Entrar como administrador y abrir «Administración» para ver existencias, pedidos, movimientos, registrar relojes y cambiar roles.

## Servicios
| Servicio | Uso interno | Dónde guarda |
|---|---|---|
| AuthApi | usuarios, contraseñas, roles y tokens JWT | esquema `identity` |
| InventoryApi | productos, ajustes, reservas, movimientos | esquema `inventory` |
| OrdersApi | pedidos, clientes y estados | esquema `orders` |
| PaymentsApi | pagos de demostración | esquema `payments` |
| Kafka | bus de eventos `shop-events` | Kafka |
| Nginx | frontend y proxy `/api/*` | no aplica |

Los cuatro esquemas viven en el mismo proyecto de Supabase. Cada API solo lee y escribe el suyo.

## Usuarios, roles y permisos
Al arrancar, `AuthApi` crea un administrador si no existe ninguno:

| Correo | Contraseña | Rol |
|---|---|---|
| `admin@tiempoyestilo.com` | `Admin123!` | admin |

Se cambian con `ADMIN_EMAIL` y `ADMIN_PASSWORD` en `.env`. Cualquier persona puede crear una cuenta de **cliente** desde «Iniciar sesión → Crear cuenta». Solo un administrador puede ascender a otro usuario (panel «Administración → Usuarios y roles»); el nuevo rol se aplica al volver a iniciar sesión.

| Acción | Sin sesión | Cliente | Admin |
|---|---|---|---|
| Ver catálogo, usar la cesta | ✔ | ✔ | ✔ |
| Crear pedido, ver y cancelar **sus** pedidos, simular **su** pago | — | ✔ | ✔ |
| Ver todos los pedidos, editarlos o eliminarlos | — | — | ✔ |
| Crear, editar, eliminar y ajustar productos; ver movimientos | — | — | ✔ |
| Listar usuarios y cambiar roles | — | — | ✔ |

`AuthApi` firma un JWT (8 horas) con el id, nombre, correo y rol. Las otras tres APIs lo validan con la misma clave (`JWT_KEY`) usando `Shared/Security.cs`, sin consultar a `AuthApi` en cada petición. Las contraseñas se guardan con PBKDF2-SHA256 y sal aleatoria.

## Cesta, validaciones y métodos de pago
- La cesta se guarda en el navegador: sobrevive a recargar la página y a cerrar sesión. Cada línea tiene botones `−`/`+`, «Quitar» y «Vaciar cesta».
- La cesta nunca supera el stock libre. Antes de enviar el pedido se vuelve a consultar el inventario; si otro cliente apartó unidades, la cesta se ajusta y se avisa.
- Validaciones (en el navegador y, de nuevo, en `OrdersApi`): nombre de 3 a 100 letras; teléfono salvadoreño de 8 dígitos que empieza por 2, 6 o 7 (acepta `+503`, espacios o guion y se guarda como `7000-0000`); dirección de 10 a 300 caracteres; método de pago válido.
- Métodos de pago (todos simulados): **tarjeta**, **transferencia** y **contra entrega**. Con tarjeta se validan titular, número (algoritmo de Luhn), vencimiento `MM/AA` no vencido y CVV (4 dígitos en Amex). **Solo se guarda la marca y los últimos 4 dígitos** (`Visa •••• 4242`); el número completo y el CVV nunca se almacenan. Tarjeta de prueba: `4242 4242 4242 4242`, vencimiento futuro, CVV `123`.

## Endpoints
Entre corchetes, quién puede usarlo. Los protegidos esperan `Authorization: Bearer <token>`.

- `POST /api/auth/register` `{ "name", "email", "password" }` [público]
- `POST /api/auth/login` `{ "email", "password" }` → `{ token, user }` [público]
- `GET /api/auth/me` [sesión]
- `GET /api/auth/users` [admin]
- `PUT /api/auth/users/{id}/role` `{ "role": "admin" | "cliente" }` [admin]
- `GET /api/inventory/products` [público]
- `POST /api/inventory/products` [admin]
- `PUT /api/inventory/products/{id}` [admin]
- `DELETE /api/inventory/products/{id}` [admin]
- `POST /api/inventory/products/{id}/adjust` `{ "change": 2, "reason": "Ingreso" }` [admin]
- `GET /api/inventory/movements` [admin]
- `POST /api/orders/orders` [sesión]
- `GET /api/orders/orders/mine` [sesión]
- `GET /api/orders/orders/{guid}` [dueño o admin]
- `POST /api/orders/orders/{guid}/cancel` [dueño o admin]
- `GET /api/orders/orders` [admin]
- `PUT /api/orders/orders/{guid}` · `DELETE /api/orders/orders/{guid}` [admin]
- `GET /api/payments/payments` · `GET /api/payments/payments/{guid}` [admin]
- `POST /api/payments/payments/{guid}/simulate` `{ "result": "approved" }` o `rejected` [dueño o admin]

Ejemplo de creación de pedido:
```bash
TOKEN=$(curl -s -X POST http://localhost:8088/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@tiempoyestilo.com","password":"Admin123!"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')

curl -X POST http://localhost:8088/api/orders/orders \
  -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -d '{"customer":"Cliente Demo","phone":"70000000","address":"San Salvador","paymentMethod":"tarjeta","card":{"holder":"Cliente Demo","number":"4242424242424242","expiry":"12/30","cvv":"123"},"items":[{"productId":1,"quantity":1}]}'
```

## Estructura
- `frontend/`: HTML, CSS, JavaScript e ilustraciones SVG propias.
- `AuthApi/`: registro, inicio de sesión y roles.
- `InventoryApi/`: inventario, reservas y consumidor Kafka.
- `OrdersApi/`: pedidos y consumidor Kafka.
- `PaymentsApi/`: cobros simulados y consumidor Kafka.
- `Shared/`: contratos y publicador/consumidor Kafka reutilizables, y autenticación JWT compartida (`Security.cs`).
- `docs/`: enfoque y decisiones, arquitectura, manual de usuario, informe académico y pruebas. La versión para entregar está en `docs/ENFOQUE.pdf`.
- `compose.yaml`: contenedores, volúmenes y dependencias.

## Persistencia
Al arrancar, cada API ejecuta sus migraciones y crea su esquema en Supabase si todavía no existe. Inventario carga los cuatro relojes cuando `inventory.Products` está vacía. `docker compose down` detiene los contenedores y **no** borra pedidos ni stock: esos datos quedan en Supabase.

Para vaciar la tienda, ejecuta esto en el editor SQL de Supabase y vuelve a levantar las APIs:

```sql
drop schema if exists inventory cascade;
drop schema if exists orders cascade;
drop schema if exists payments cascade;
drop schema if exists identity cascade;
```

## Avisos importantes
**Solo para demostración académica/local.** Tiene login, roles y validaciones, pero no controles de producción: no hay HTTPS, límite de intentos de login, recuperación de contraseña ni pasarela bancaria. Cambia `JWT_KEY` y `ADMIN_PASSWORD` si la tienda sale de tu equipo. No publicar en Internet ni introducir datos personales o tarjetas reales. No se realizan cargos reales. Los mensajes Kafka se procesan de forma asíncrona y pueden tardar unos segundos.

**Limitación de consistencia:** el proyecto es un prototipo educativo; no implementa outbox transaccional, reconciliación automática, expiración de reservas, ni coordinación robusta ante fallas entre guardar en Supabase y publicar en Kafka. Para producción se requieren estos mecanismos, además de seguridad, auditoría, idempotencia de solicitudes HTTP y pruebas de concurrencia.

## Validación
`dotnet build` compila Shared, AuthApi, InventoryApi, OrdersApi y PaymentsApi. `docker compose up --build -d` publica la tienda en el puerto 8088. Al crear un pedido, OrdersApi publica `OrderPlaced`; InventoryApi reserva existencias y el estado pasa a `awaiting-payment`; al simular el pago aprobado el pedido queda `paid` y el stock baja una unidad. El plan de casos está en `docs/PRUEBAS.md`.
