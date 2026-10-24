# Plan de pruebas manuales

## Preparación
Ejecutar `docker compose up --build -d`; comprobar `docker compose ps` y `docker compose logs --tail=100`.

## Casos
| ID | Prueba | Resultado esperado |
|---|---|---|
| P01 | Abrir tienda | Se muestran 4 relojes iniciales |
| P02 | Buscar «Rose» | Se filtra el catálogo |
| P03 | Añadir al carrito | El contador y total aumentan |
| P04 | Registrar pedido con stock | Estado pasa de pending-stock a awaiting-payment |
| P05 | Simular pago aprobado | Pedido paid y stock disminuye |
| P06 | Simular pago rechazado | Pedido payment-failed y se libera reserva |
| P07 | Intentar más unidades que stock | Pedido rechazado por inventario o cliente impide añadir |
| P08 | Ajustar stock desde administración | Cantidad cambia y se registra movimiento |
| P09 | Registrar un reloj | Nuevo reloj aparece en catálogo |
| P10 | Reiniciar contenedores sin -v | Los datos permanecen |

## Resultados observados
En una ejecución con `docker compose up --build -d`:

- P01: el catálogo muestra los cuatro relojes iniciales.
- P02: buscar «Rose» deja solo Casio Rose Gold Diamond.
- P04 y P05: un pedido de una unidad pasa a `awaiting-payment` y, tras simular el pago aprobado, queda `paid`. El stock de ese reloj baja de 12 a 11.
- P06: un pago rechazado deja el pedido en `payment-failed` y libera la reserva (el stock no disminuye).
- P07: pedir 50 unidades de un reloj con 6 en existencia deja el pedido en `rejected`.

La interfaz en http://localhost:8088 recorre el mismo flujo: carrito, «Crear pedido», «Simular pago aprobado» y el panel de administración.

## Observaciones
Los demás casos siguen siendo un plan manual. Probar también mensajes duplicados, reinicios a mitad del flujo y cancelaciones: estas situaciones requieren mejoras antes de un uso real.
