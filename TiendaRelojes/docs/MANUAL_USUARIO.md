# Manual de usuario

## Cliente
1. Entra en `http://localhost:8088`.
2. Revisa el catálogo y utiliza el buscador.
3. Pulsa un reloj y luego «Añadir a la cesta». En la cesta usa `−`/`+` para cambiar cantidades; la cesta se recuerda aunque recargues la página.
4. Pulsa «Iniciar sesión» y crea una cuenta (contraseña de 8 caracteres o más, con letras y números).
5. Completa los datos ficticios de envío y elige tarjeta, transferencia o contra entrega. Para tarjeta usa `4242 4242 4242 4242`, una fecha futura y CVV `123`.
6. Crea el pedido y espera el resultado de la verificación de stock.
7. Cuando aparezca «Pendiente de pago», simula aprobación o rechazo.
8. Comprueba que el estado se actualiza. «Mis pedidos» muestra todos tus pedidos.

## Administrador (solo demo local)
1. Inicia sesión con `admin@tiempoyestilo.com` / `Admin123!` y pulsa «Administración» (solo aparece para el rol admin).
2. Revisa el inventario y los pedidos recientes.
3. Usa los botones `+1` y `-1` para ajustar existencias.
4. Rellena «Agregar reloj» para registrar otro modelo.
5. Revisa movimientos para comprobar las salidas por venta.
6. En «Usuarios y roles» cambia el rol de una cuenta. El usuario debe volver a iniciar sesión.

## Errores habituales
- «No se pudo conectar»: esperar a que Docker levante Kafka y las APIs.
- «Stock insuficiente»: seleccionar otra cantidad o ajustar existencias.
- Pago deshabilitado: el pedido aún está esperando la reserva de inventario.
- Tras reiniciar: los datos permanecen si no se borran los volúmenes.
- «Tu sesión terminó»: el token dura 8 horas; vuelve a iniciar sesión.
- «Tu usuario no tiene permiso»: esa acción es solo para administradores.

No usar datos personales reales: esta aplicación no dispone de controles de seguridad de producción.
