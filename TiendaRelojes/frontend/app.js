
/* =========================================================
   1. VARIABLES GLOBALES Y UTILIDADES
========================================================= */

const $ = s => document.querySelector(s);

const money = n => '$' + Number(n).toFixed(2);

let products = [];
let cart = [];
let currentOrder = null;


/* =========================================================
   2. COMUNICACIÓN CON LA API
========================================================= */

const api = async (path, opts = {}) => {
    const r = await fetch(path, {
        headers: {
            'Content-Type': 'application/json'
        },
        ...opts
    });

    if (!r.ok) {
        throw Error(await r.text());
    }

    return r.status === 204 ? null : r.json();
};

const post = (url, data) => api(url, {
    method: 'POST',
    body: JSON.stringify(data)
});

const put = (url, data) => api(url, {
    method: 'PUT',
    body: JSON.stringify(data)
});

const remove = (url) => api(url, {
    method: 'DELETE'
});


/* =========================================================
   3. SEGURIDAD Y VENTANAS MODALES
========================================================= */

const safe = s => String(s ?? '').replace(
    /[&<>"']/g,
    c => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[c])
);

function show(id) {
    $('#' + id).showModal();
}

function close(id) {
    $('#' + id).close();
}

document.querySelectorAll('[data-close]').forEach(b => {
    b.onclick = () => close(b.dataset.close);
});


/* =========================================================
   4. CARGA DE PRODUCTOS
========================================================= */

async function load() {
    try {
        products = await api('/api/inventory/products');

        renderProducts();
        renderAdminProducts();

    } catch (e) {
        $('#products').textContent =
            'No se pudo conectar con los servicios. Verifica Docker.';

        console.error(e);
    }
}


/* =========================================================
   5. CATÁLOGO DE PRODUCTOS
========================================================= */

function cartQuantity(id) {
    return cart.find(item => item.id === id)?.quantity || 0;
}

function availableUnits(p) {
    return Math.max(0, p.stock - p.reserved);
}

function remainingUnits(p) {
    return Math.max(0, availableUnits(p) - cartQuantity(p.id));
}

function stockLabel(p) {
    const available = availableUnits(p);
    if (available > 0) return `${available} disponibles`;
    if (p.reserved > 0) return 'Apartado por un pedido';
    return 'Agotado';
}

function renderProducts() {
    const term = $('#search').value.toLowerCase();

    const matched = products.filter(p =>
        p.name.toLowerCase().includes(term) ||
        p.category.toLowerCase().includes(term)
    );
    const emptyMessage = products.length
        ? 'Ningún reloj coincide con la búsqueda.'
        : 'No hay relojes en el catálogo.';

    $('#products').innerHTML = matched.length
        ? matched
        .map(p => `
            <article class="product" data-id="${p.id}">
                <img
                    src="${safe(p.image || '/images/placeholder.svg')}"
                    alt="${safe(p.name)}"
                >

                <h3>${safe(p.name)}</h3>

                <p>${money(p.price)}</p>

                <span class="muted">${stockLabel(p)}</span>
                ${p.reserved > 0
                    ? `<span class="reserve">${p.reserved} reservado${p.reserved === 1 ? '' : 's'}</span>`
                    : ''}

                <button
                    class="primary add-btn"
                    type="button"
                    data-add="${p.id}"
                    ${remainingUnits(p) > 0 ? '' : 'disabled'}
                >
                    ${remainingUnits(p) > 0
                        ? 'Añadir a la cesta'
                        : availableUnits(p) > 0
                            ? 'Ya están en la cesta'
                            : p.reserved > 0
                                ? 'Reservado'
                                : 'Sin existencias'}
                </button>
            </article>
        `)
        .join('')
        : `<p class="muted catalog-empty">${emptyMessage}</p>`;

    document.querySelectorAll('.product').forEach(el => {
        el.onclick = event => {
            if (event.target.closest('[data-add]')) return;
            detail(+el.dataset.id);
        };
    });

    document.querySelectorAll('[data-add]').forEach(button => {
        button.onclick = event => {
            event.stopPropagation();
            add(+button.dataset.add);
        };
    });
}


/* =========================================================
   6. DETALLE DE PRODUCTO
========================================================= */

function detail(id) {
    const p = products.find(x => x.id === id);

    if (!p) return;

    $('#productDetail').innerHTML = `
        <div class="detail">
            <img
                src="${safe(p.image || '/images/placeholder.svg')}"
                alt="${safe(p.name)}"
            >

            <div>
                <h2>${safe(p.name)}</h2>

                <p class="price">
                    ${money(p.price)}
                </p>

                <p>${safe(p.description)}</p>

                <p class="muted">${stockLabel(p)}</p>
                ${p.reserved > 0
                    ? `<p class="reserve">${p.reserved} reservado${p.reserved === 1 ? '' : 's'} por pedidos en curso</p>`
                    : ''}

                <button
                    id="addOne"
                    class="primary"
                    ${remainingUnits(p) <= 0 ? 'disabled' : ''}
                >
                    ${remainingUnits(p) > 0
                        ? 'Añadir a la cesta'
                        : availableUnits(p) > 0
                            ? 'Ya están en la cesta'
                            : (p.reserved > 0 ? 'Reservado' : 'Sin existencias')}
                </button>
            </div>
        </div>
    `;

    $('#addOne').onclick = () => {
        add(id);
        if ($('#productDialog').open) detail(id);
    };

    show('productDialog');
}


/* =========================================================
   7. AGREGAR PRODUCTOS AL CARRITO
========================================================= */

function add(id) {
    const p = products.find(x => x.id === id);
    const available = p ? availableUnits(p) : 0;
    const already = cartQuantity(id);

    if (!p || available <= 0) {
        note(
            p
                ? `No hay existencias de ${p.name}.`
                : 'Ese reloj ya no está en el catálogo.',
            true
        );
        return;
    }

    if (already >= available) {
        note(`Ya están en la cesta las ${available} unidades disponibles de ${p.name}.`, true);
        return;
    }

    const line = cart.find(item => item.id === id);
    if (line) {
        line.quantity += 1;
    } else {
        cart.push({
            id,
            quantity: 1
        });
    }

    updateCart();
    note(`${p.name} quedó en la cesta (${already + 1}). Puedes añadirlo otra vez.`, false);
}

function note(text, isError) {
    const box = $('#cartNotice');
    box.textContent = text;
    box.className = isError ? 'notice error' : 'notice success';
}


/* =========================================================
   8. ACTUALIZACIÓN DEL CARRITO
========================================================= */

function updateCart() {
    cart = cart.filter(x =>
        products.some(p => p.id === x.id)
    );

    $('#cartCount').textContent = cart.reduce(
        (s, x) => s + x.quantity,
        0
    );

    $('#cartLines').innerHTML = cart.length
        ? cart.map(x => {
            const p = products.find(p => p.id === x.id);

            return `
                <div class="cart-row">
                    <span>
                        ${safe(p.name)} × ${x.quantity}
                    </span>

                    <span>
                        ${money(p.price * x.quantity)}

                        <button
                            class="smallbtn"
                            data-remove="${x.id}"
                        >
                            Quitar
                        </button>
                    </span>
                </div>
            `;
        }).join('')
        : 'Tu cesta está vacía';

    document.querySelectorAll('[data-remove]').forEach(b => {
        b.onclick = () => {
            cart = cart.filter(
                x => x.id !== +b.dataset.remove
            );

            updateCart();
        };
    });

    $('#cartTotal').textContent = money(
        cart.reduce(
            (s, x) =>
                s + x.quantity *
                products.find(p => p.id === x.id).price,
            0
        )
    );

    renderProducts();
}


/* =========================================================
   9. EVENTOS DEL CATÁLOGO Y CARRITO
========================================================= */

$('#search').oninput = renderProducts;

$('#cartBtn').onclick = () => {
    updateCart();
};

$('#adminBtn').onclick = async () => {
    await refreshAdmin();
    show('adminDialog');
};


/* =========================================================
   10. REGISTRO DE PEDIDOS
========================================================= */

$('#checkout').onsubmit = event => event.preventDefault();

function setOrderButton(busy) {
    const button = $('#placeOrder');
    button.disabled = busy;
    button.classList.toggle('is-busy', busy);
    button.setAttribute('aria-busy', busy ? 'true' : 'false');
    button.textContent = busy ? 'Creando pedido...' : 'Crear pedido';
}

$('#placeOrder').onclick = async () => {
    const form = $('#checkout');
    const message = $('#checkoutMsg');

    if (!cart.length) {
        note('La cesta está vacía. Añade un reloj antes de crear el pedido.', true);
        message.textContent = 'Añade un reloj para poder crear el pedido.';
        message.className = 'error';
        return;
    }

    if (!form.reportValidity()) {
        message.textContent = 'Completa nombre, teléfono y dirección. El pedido todavía no se envía.';
        message.className = 'error';
        return;
    }

    const data = new FormData(form);
    setOrderButton(true);
    note('Creando el pedido. Esto puede tardar unos segundos.', false);
    message.textContent = 'Enviando el pedido y publicando el evento...';
    message.className = 'notice busy-msg';

    try {
        const order = await post('/api/orders/orders', {
            customer: data.get('customer'),
            phone: data.get('phone'),
            address: data.get('address'),
            paymentMethod: data.get('paymentMethod'),
            items: cart.map(item => ({
                productId: item.id,
                quantity: item.quantity
            }))
        });

        const orderId = order?.id || order?.Id;
        if (!orderId) {
            throw Error('El servicio de pedidos no devolvió el número de pedido.');
        }

        currentOrder = orderId;
        cart = [];
        updateCart();
        form.reset();
        note('Pedido creado. Sigue el estado en la ventana de seguimiento.', false);
        message.textContent = 'Pedido creado. Abriendo el seguimiento...';
        message.className = 'success';

        if (!$('#orderDialog').open) show('orderDialog');
        await refreshOrder();

    } catch (err) {
        message.textContent = 'No se pudo crear el pedido: ' + err.message;
        message.className = 'error';
        note('El pedido no se registró. Revisa el mensaje bajo el botón.', true);

    } finally {
        setOrderButton(false);
    }
};


/* =========================================================
   11. ESTADOS DE LOS PEDIDOS
========================================================= */

const statuses = {
    'pending-stock': 'Verificando inventario',
    'awaiting-payment': 'Inventario reservado · Pendiente de pago',
    'rejected': 'Sin stock suficiente',
    'paid': 'Pago confirmado · Pedido registrado',
    'payment-failed': 'Pago rechazado',
    'cancelled': 'Pedido cancelado'
};


/* =========================================================
   12. CONSULTAR ESTADO DEL PEDIDO
========================================================= */

let catalogSyncKey = '';

function reservationNote(status) {
    if (status === 'pending-stock') {
        return 'Kafka está apartando estos relojes en inventario.';
    }
    if (status === 'awaiting-payment') {
        return 'Kafka ya reservó estas piezas. Siguen apartadas hasta el pago.';
    }
    if (status === 'paid') {
        return 'El pago confirmó la venta. La reserva salió del inventario.';
    }
    if (status === 'rejected') {
        return 'No había existencias libres. Kafka no dejó ninguna reserva.';
    }
    if (status === 'payment-failed' || status === 'cancelled') {
        return 'La reserva se liberó y los relojes volvieron a estar disponibles.';
    }
    return '';
}

async function refreshOrder() {
    if (!currentOrder) return;

    try {
        const o = await api(
            '/api/orders/orders/' + currentOrder
        );
        const items = o.items || [];
        const lines = items.map(item => `
            <li>${safe(item.name)} × ${item.quantity}</li>
        `).join('');

        $('#orderStatus').innerHTML = `
            <div class="status">
                <p>
                    <b>Pedido:</b> ${safe(o.id)}
                </p>

                <p>
                    <b>Estado:</b>
                    ${safe(statuses[o.status] || o.status)}
                </p>

                <p>
                    <b>Total:</b> ${money(o.total)}
                </p>

                <p>
                    <b>Forma de pago:</b>
                    ${safe(o.paymentMethod)}
                </p>

                ${lines ? `<p><b>Relojes del pedido</b></p><ul class="reserve-list">${lines}</ul>` : ''}
                <p class="reserve">${safe(reservationNote(o.status))}</p>
            </div>

            <p class="muted">
                Los pagos son simulados y no transfieren dinero.
            </p>
        `;

        const enabled = o.status === 'awaiting-payment';

        $('#approvePayment').disabled = !enabled;
        $('#rejectPayment').disabled = !enabled;

        const syncKey = `${o.id}:${o.status}`;
        if (
            syncKey !== catalogSyncKey &&
            (
                o.status === 'awaiting-payment' ||
                o.status === 'paid' ||
                o.status === 'rejected' ||
                o.status === 'payment-failed'
            )
        ) {
            catalogSyncKey = syncKey;
            await load();
        }

    } catch (e) {
        $('#orderStatus').textContent = e.message;
    }
}


/* =========================================================
   13. SIMULACIÓN DE PAGOS
========================================================= */

async function waitForOrderStatus(expected) {
    for (let attempt = 0; attempt < 8; attempt++) {
        try {
            const order = await api('/api/orders/orders/' + currentOrder);
            if (expected.includes(order.status)) return order;
        } catch (error) {
            console.error(error);
        }
        await new Promise(resolve => setTimeout(resolve, 400));
    }
    return null;
}

async function simulate(result) {
    const approve = $('#approvePayment');
    const reject = $('#rejectPayment');
    approve.disabled = true;
    reject.disabled = true;

    if (result === 'approved') {
        approve.classList.add('is-busy');
        approve.textContent = 'Confirmando pago...';
    }

    try {
        await post(
            '/api/payments/payments/' +
            currentOrder +
            '/simulate',
            { result }
        );

        if (result === 'approved') {
            await waitForOrderStatus(['paid']);
            await load();
            if ($('#orderDialog').open) close('orderDialog');
            note('Pago aprobado. El pedido quedó registrado.', false);
            $('#checkoutMsg').textContent = 'Pago aprobado. Puedes seguir comprando.';
            $('#checkoutMsg').className = 'success';
            return;
        }

        await refreshOrder();

    } catch (e) {
        $('#orderStatus').innerHTML = `<p class="error">No se pudo simular el pago: ${safe(e.message)}</p>`;

    } finally {
        approve.classList.remove('is-busy');
        approve.textContent = 'Simular pago aprobado';
        if ($('#orderDialog').open) await refreshOrder();
    }
}

$('#approvePayment').onclick = () => {
    simulate('approved');
};

$('#rejectPayment').onclick = () => {
    simulate('rejected');
};


/* =========================================================
   14. ACTUALIZACIÓN AUTOMÁTICA DEL PEDIDO
========================================================= */

let adminTick = false;

setInterval(() => {
    if ($('#orderDialog').open) {
        refreshOrder();
    }
    if ($('#adminDialog').open && !adminTick) {
        adminTick = true;
        refreshAdmin().finally(() => {
            adminTick = false;
        });
    }
}, 2500);


/* =========================================================
   15. ADMINISTRACIÓN DE INVENTARIO
========================================================= */

function renderAdminProducts() {
    $('#adminProducts').innerHTML = products
        .map(p => `
            <div class="admin-row">
                <span>
                    ${safe(p.name)}

                    <br>

                    <small class="${p.reserved > 0 ? 'reserve' : ''}">
                        Stock ${p.stock},
                        reservado ${p.reserved}
                    </small>
                </span>

                <span>
                    <button
                        class="smallbtn"
                        data-adjust="${p.id}"
                        data-change="1"
                    >
                        +1
                    </button>

                    <button
                        class="smallbtn"
                        data-adjust="${p.id}"
                        data-change="-1"
                    >
                        -1
                    </button>
                </span>
            </div>
        `)
        .join('');

    document.querySelectorAll('[data-adjust]').forEach(b => {
        b.onclick = async () => {
            try {
                await post(
                    `/api/inventory/products/${b.dataset.adjust}/adjust`,
                    {
                        change: +b.dataset.change,
                        reason: 'Ajuste manual'
                    }
                );

                await load();

            } catch (e) {
                alert(e.message);
            }
        };
    });
}


/* =========================================================
   16. ACTUALIZACIÓN DEL PANEL ADMINISTRATIVO
========================================================= */

const reservationLabels = {
    pending: 'Kafka está apartando',
    reserved: 'Reservado ahora',
    sold: 'Reservado y luego vendido',
    released: 'Reserva liberada'
};

function reservationsFromOrders(orders) {
    return orders.flatMap(order => {
        const status = order.status === 'pending-stock'
            ? 'pending'
            : order.status === 'awaiting-payment'
                ? 'reserved'
                : order.status === 'paid'
                    ? 'sold'
                    : 'released';

        return (order.items || []).map(item => ({
            status,
            productId: item.productId,
            quantity: item.quantity,
            orderId: order.id
        }));
    });
}

function renderReservations(rows) {
    const box = $('#adminReservations');
    if (!box) return;

    const active = rows.filter(row => row.status === 'reserved');
    box.innerHTML = rows.length
        ? `
            <p class="${active.length ? 'reserve' : 'muted'}">
                ${active.length
                    ? `${active.reduce((sum, row) => sum + row.quantity, 0)} reloj(es) apartados en este momento.`
                    : 'No hay apartados activos. Al pagar, la reserva pasa a venta y el contador vuelve a 0.'}
            </p>
            ${rows.slice(0, 8).map(row => {
                const product = products.find(item => item.id === row.productId);
                return `
                    <div class="admin-row">
                        <span>
                            ${safe(product?.name || 'Producto #' + row.productId)} × ${row.quantity}
                            <br>
                            <small>${safe(reservationLabels[row.status] || row.status)}</small>
                        </span>
                        <b>${safe(String(row.orderId).slice(0, 8))}</b>
                    </div>
                `;
            }).join('')}
        `
        : '<p class="muted">Kafka todavía no ha reservado ningún reloj.</p>';
}

let latestOrders = [];
let editingOrderId = null;

function renderAdminOrders(orders) {
    latestOrders = orders;
    const box = $('#adminOrders');

    box.innerHTML = orders.slice(0, 12).map(order => `
        <div class="admin-row">
            <span>
                ${safe(order.customer)}
                <br>
                <small>${safe(statuses[order.status] || order.status)}</small>
            </span>
            <span class="order-actions">
                <b>${money(order.total)}</b>
                <button type="button" class="smallbtn" data-edit="${order.id}">Editar</button>
                <button type="button" class="smallbtn" data-delete="${order.id}">Eliminar</button>
            </span>
        </div>
    `).join('') || 'Sin pedidos';

    box.querySelectorAll('[data-edit]').forEach(button => {
        button.onclick = () => openOrderEditor(button.dataset.edit);
    });

    box.querySelectorAll('[data-delete]').forEach(button => {
        button.onclick = () => deleteOrder(button.dataset.delete);
    });
}

function openOrderEditor(id) {
    const order = latestOrders.find(item => item.id === id);
    const editor = $('#orderEditor');
    if (!order || !editor) return;

    editingOrderId = id;
    const canChangeStock = order.status === 'awaiting-payment' || order.status === 'paid';

    editor.innerHTML = `
        <form class="order-editor" id="editOrderForm">
            <h4>Pedido ${safe(String(order.id).slice(0, 8))}</h4>
            <p class="muted">
                ${canChangeStock
                    ? 'Si pides más unidades, Kafka aparta o descuenta stock. Si pides menos, Kafka devuelve las unidades.'
                    : 'Este pedido ya no modifica el inventario. Puedes eliminarlo.'}
            </p>
            <label>Nombre<input name="customer" value="${safe(order.customer)}" required></label>
            <label>Teléfono<input name="phone" value="${safe(order.phone)}" required></label>
            <label>Dirección<input name="address" value="${safe(order.address)}" required></label>
            <label>Pago
                <select name="paymentMethod">
                    <option value="transferencia" ${order.paymentMethod === 'transferencia' ? 'selected' : ''}>Transferencia</option>
                    <option value="contra-entrega" ${order.paymentMethod === 'contra-entrega' ? 'selected' : ''}>Contra entrega</option>
                </select>
            </label>
            ${(order.items || []).map(item => `
                <div class="qty-row">
                    <span>${safe(item.name)} · ${money(item.unitPrice)}</span>
                    <input name="qty-${item.productId}" type="number" min="0" max="100" value="${item.quantity}" ${canChangeStock ? '' : 'disabled'}>
                </div>
            `).join('')}
            <p class="muted">Cantidad 0 quita ese reloj del pedido.</p>
            <p id="editOrderMsg"></p>
            <span class="order-actions">
                ${canChangeStock ? '<button class="primary" type="submit">Guardar cambios</button>' : ''}
                <button type="button" id="closeOrderEditor">Cerrar</button>
            </span>
        </form>
    `;

    $('#closeOrderEditor').onclick = () => {
        editingOrderId = null;
        editor.innerHTML = '';
    };

    const form = $('#editOrderForm');
    form.onsubmit = async event => {
        event.preventDefault();
        const message = $('#editOrderMsg');
        const data = new FormData(form);
        const items = (order.items || []).map(item => ({
            productId: item.productId,
            quantity: Number(data.get('qty-' + item.productId) || 0)
        })).filter(item => item.quantity > 0);

        if (!items.length) {
            message.textContent = 'Deja al menos un reloj, o elimina el pedido completo.';
            return;
        }

        message.textContent = 'Guardando y avisando a Kafka...';

        try {
            await put('/api/orders/orders/' + order.id, {
                customer: data.get('customer'),
                phone: data.get('phone'),
                address: data.get('address'),
                paymentMethod: data.get('paymentMethod'),
                items
            });
            editingOrderId = null;
            editor.innerHTML = '<p class="success">Pedido actualizado. Kafka está ajustando el inventario.</p>';
            await refreshAdmin();
        } catch (error) {
            message.textContent = error.message || 'No se pudo guardar el pedido.';
        }
    };
}

async function deleteOrder(id) {
    const order = latestOrders.find(item => item.id === id);
    const changesStock = order && (
        order.status === 'pending-stock' ||
        order.status === 'awaiting-payment' ||
        order.status === 'paid'
    );
    const confirmed = confirm(
        changesStock
            ? 'Se eliminará el pedido y Kafka devolverá las unidades al inventario.'
            : 'Se eliminará el pedido. El inventario no cambia porque ya estaba cerrado.'
    );

    if (!confirmed) return;

    try {
        await remove('/api/orders/orders/' + id);
        if (editingOrderId === id) {
            editingOrderId = null;
            const editor = $('#orderEditor');
            if (editor) editor.innerHTML = '';
        }
        await refreshAdmin();
    } catch (error) {
        alert(error.message || 'No se pudo eliminar el pedido.');
    }
}

async function refreshAdmin() {
    await load();

    try {
        const [orders, moves] = await Promise.all([
            api('/api/orders/orders'),
            api('/api/inventory/movements')
        ]);

        renderReservations(reservationsFromOrders(orders));
        renderAdminOrders(orders);

        // Últimos 10 movimientos de inventario
        $('#adminMovements').innerHTML = moves
            .slice(0, 10)
            .map(m => `
                <div class="admin-row">
                    <span>
                        Producto #${m.productId} ·
                        ${safe(m.reason)}
                    </span>

                    <b>${m.change}</b>
                </div>
            `)
            .join('') || 'Sin movimientos';

    } catch (e) {
        console.error(e);
    }
}


/* =========================================================
   17. REGISTRO DE NUEVOS PRODUCTOS
========================================================= */

$('#newProduct').onsubmit = async e => {
    e.preventDefault();

    const f = new FormData(e.target);

    try {
        await post('/api/inventory/products', {
            name: f.get('name'),
            category: f.get('category'),
            price: Number(f.get('price')),
            stock: Number(f.get('stock')),
            description: f.get('description') || '',
            image: '/images/placeholder.svg'
        });

        e.target.reset();

        await refreshAdmin();

    } catch (e) {
        alert(e.message);
    }
};


/* =========================================================
   18. INICIALIZACIÓN DE LA APLICACIÓN
========================================================= */

load();
