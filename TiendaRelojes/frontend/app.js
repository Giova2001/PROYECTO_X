
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

function renderProducts() {
    const term = $('#search').value.toLowerCase();

    $('#products').innerHTML = products
        .filter(p =>
            p.name.toLowerCase().includes(term) ||
            p.category.toLowerCase().includes(term)
        )
        .map(p => `
            <article class="product" data-id="${p.id}">
                <img
                    src="${safe(p.image || '/images/placeholder.svg')}"
                    alt="${safe(p.name)}"
                >

                <h3>${safe(p.name)}</h3>

                <p>${money(p.price)}</p>

                <span class="muted">
                    ${p.stock - p.reserved > 0
                        ? 'Disponible'
                        : 'Agotado'}
                </span>

                <button
                    class="primary add-btn"
                    type="button"
                    data-add="${p.id}"
                    ${p.stock - p.reserved > 0 ? '' : 'disabled'}
                >
                    ${p.stock - p.reserved > 0
                        ? 'Añadir a la cesta'
                        : 'Sin existencias'}
                </button>
            </article>
        `)
        .join('');

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

                <p class="muted">
                    ${p.stock - p.reserved} unidades disponibles
                </p>

                <button
                    id="addOne"
                    class="primary"
                    ${p.stock - p.reserved <= 0 ? 'disabled' : ''}
                >
                    Añadir a la cesta
                </button>
            </div>
        </div>
    `;

    $('#addOne').onclick = () => {
        add(id);
        if ($('#productDialog').open) close('productDialog');
    };

    show('productDialog');
}


/* =========================================================
   7. AGREGAR PRODUCTOS AL CARRITO
========================================================= */

function add(id) {
    const p = products.find(x => x.id === id);
    const row = cart.find(x => x.id === id);

    const available = p ? p.stock - p.reserved : 0;

    if (!p || ((row?.quantity || 0) >= available)) {
        note(
            p
                ? `No hay más existencias de ${p.name}.`
                : 'Ese reloj ya no está en el catálogo.',
            true
        );
        return;
    }

    if (row) {
        row.quantity++;
    } else {
        cart.push({
            id,
            quantity: 1
        });
    }

    updateCart();
    note(`${p.name} se agregó a la cesta.`, false);
    document.getElementById('cesta').scrollIntoView({
        behavior: 'smooth',
        block: 'nearest'
    });
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

$('#placeOrder').onclick = async () => {
    const form = $('#checkout');

    if (!cart.length) {
        note('La cesta está vacía. Añade un reloj antes de crear el pedido.', true);
        $('#checkoutMsg').textContent = '';
        return;
    }

    if (!form.reportValidity()) return;

    const data = new FormData(form);
    const button = $('#placeOrder');

    button.disabled = true;
    $('#checkoutMsg').textContent = 'Registrando pedido...';
    $('#checkoutMsg').className = '';

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

        if (!order?.id) {
            throw Error('El servicio de pedidos no devolvió el número de pedido.');
        }

        currentOrder = order.id;
        cart = [];
        updateCart();
        form.reset();
        note('Pedido creado. Sigue el estado en la ventana de seguimiento.', false);
        $('#checkoutMsg').textContent = '';

        if (!$('#orderDialog').open) show('orderDialog');
        await refreshOrder();

    } catch (err) {
        $('#checkoutMsg').textContent = 'No se pudo crear el pedido: ' + err.message;
        $('#checkoutMsg').className = 'error';

    } finally {
        button.disabled = false;
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

async function refreshOrder() {
    if (!currentOrder) return;

    try {
        const o = await api(
            '/api/orders/orders/' + currentOrder
        );

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
            </div>

            <p class="muted">
                Los pagos son simulados y no transfieren dinero.
            </p>
        `;

        const enabled = o.status === 'awaiting-payment';

        $('#approvePayment').disabled = !enabled;
        $('#rejectPayment').disabled = !enabled;

        if (
            o.status === 'paid' ||
            o.status === 'rejected' ||
            o.status === 'payment-failed'
        ) {
            await load();
        }

    } catch (e) {
        $('#orderStatus').textContent = e.message;
    }
}


/* =========================================================
   13. SIMULACIÓN DE PAGOS
========================================================= */

async function simulate(result) {
    try {
        await post(
            '/api/payments/payments/' +
            currentOrder +
            '/simulate',
            { result }
        );

        await refreshOrder();

    } catch (e) {
        alert(e.message);
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

setInterval(() => {
    if ($('#orderDialog').open) {
        refreshOrder();
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

                    <small>
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

async function refreshAdmin() {
    await load();

    try {
        const [orders, moves] = await Promise.all([
            api('/api/orders/orders'),
            api('/api/inventory/movements')
        ]);

        // Últimos 12 pedidos
        $('#adminOrders').innerHTML = orders
            .slice(0, 12)
            .map(o => `
                <div class="admin-row">
                    <span>
                        ${safe(o.customer)}

                        <br>

                        <small>
                            ${safe(statuses[o.status] || o.status)}
                        </small>
                    </span>

                    <b>${money(o.total)}</b>
                </div>
            `)
            .join('') || 'Sin pedidos';

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
