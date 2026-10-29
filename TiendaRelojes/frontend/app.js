
/* =========================================================
   1. VARIABLES GLOBALES Y UTILIDADES
========================================================= */

const $ = s => document.querySelector(s);

const money = n => '$' + Number(n).toFixed(2);

// localStorage puede fallar (modo privado, bloqueo de cookies):
// la tienda sigue funcionando, solo sin recordar sesión ni cesta.
const store = {
    read(key, fallback) {
        try {
            return JSON.parse(localStorage.getItem(key)) ?? fallback;
        } catch {
            return fallback;
        }
    },
    write(key, value) {
        try {
            if (value === null) localStorage.removeItem(key);
            else localStorage.setItem(key, JSON.stringify(value));
        } catch {
            /* sin almacenamiento */
        }
    }
};

let products = [];
let cart = store.read('te-cart', []).filter(item =>
    Number.isInteger(item?.id) && Number.isInteger(item?.quantity) && item.quantity > 0
);
let currentOrder = null;

// { token, user: { id, name, email, role } } emitido por AuthApi.
let session = store.read('te-session', null);

const isAdmin = () => session?.user?.role === 'admin';


/* =========================================================
   2. COMUNICACIÓN CON LA API
========================================================= */

// Convierte la respuesta de error en un texto legible.
// Las APIs devuelven "texto" en JSON o un ProblemDetails.
async function errorMessage(r) {
    const text = await r.text();
    let message = text;

    try {
        const body = JSON.parse(text);
        message = typeof body === 'string'
            ? body
            : body?.detail || body?.title || text;
    } catch {
        /* texto plano */
    }

    if (message) return message;
    if (r.status === 401) return 'Inicia sesión para continuar.';
    if (r.status === 403) return 'Tu usuario no tiene permiso para esta acción.';
    return `Error ${r.status}`;
}

const api = async (path, opts = {}) => {
    const headers = {
        'Content-Type': 'application/json'
    };

    if (session?.token) {
        headers.Authorization = 'Bearer ' + session.token;
    }

    const r = await fetch(path, {
        headers,
        ...opts
    });

    if (!r.ok) {
        const message = await errorMessage(r);

        // Token vencido o inválido: se cierra la sesión local.
        if (r.status === 401 && session && !path.startsWith('/api/auth/login')) {
            setSession(null);
            throw Error('Tu sesión terminó. Inicia sesión de nuevo.');
        }

        throw Error(message);
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

        // updateCart ajusta la cesta al stock nuevo y redibuja el catálogo.
        updateCart();
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
    bumpCartCount();
}

// Con la cesta cerrada, el contador de la cabecera avisa de que se añadió algo.
function bumpCartCount() {
    const count = $('#cartCount');
    count.classList.remove('bump');
    void count.offsetWidth;
    count.classList.add('bump');
}

function note(text, isError) {
    const box = $('#cartNotice');
    box.textContent = text;
    box.className = isError ? 'notice error' : 'notice success';
}


/* =========================================================
   8. ACTUALIZACIÓN DEL CARRITO
========================================================= */

// Quita de la cesta lo que ya no existe y baja las cantidades
// que superan el stock libre. Devuelve los nombres ajustados.
function fitCartToStock() {
    const adjusted = [];

    cart = cart
        .filter(x => products.some(p => p.id === x.id))
        .map(x => {
            const p = products.find(p => p.id === x.id);
            const available = availableUnits(p);
            if (x.quantity > available) {
                adjusted.push(p.name);
                return { ...x, quantity: available };
            }
            return x;
        })
        .filter(x => x.quantity > 0);

    return adjusted;
}

function updateCart() {
    const adjusted = fitCartToStock();
    if (adjusted.length) {
        note(`Ajustamos la cesta al stock disponible: ${adjusted.join(', ')}.`, true);
    }

    store.write('te-cart', cart);

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
                        ${safe(p.name)}
                        <small>${money(p.price)} c/u</small>
                        <span class="qty-control">
                            <button type="button" class="smallbtn" data-dec="${x.id}" aria-label="Quitar una unidad">−</button>
                            <b>${x.quantity}</b>
                            <button type="button" class="smallbtn" data-inc="${x.id}" aria-label="Añadir una unidad"
                                ${remainingUnits(p) > 0 ? '' : 'disabled'}>+</button>
                        </span>
                    </span>

                    <span>
                        ${money(p.price * x.quantity)}

                        <button
                            type="button"
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

    document.querySelectorAll('[data-inc]').forEach(b => {
        b.onclick = () => add(+b.dataset.inc);
    });

    document.querySelectorAll('[data-dec]').forEach(b => {
        b.onclick = () => {
            const line = cart.find(x => x.id === +b.dataset.dec);
            if (!line) return;
            line.quantity -= 1;
            cart = cart.filter(x => x.quantity > 0);
            updateCart();
        };
    });

    $('#clearCart').hidden = !cart.length;

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

$('#clearCart').onclick = () => {
    if (!confirm('¿Vaciar la cesta?')) return;
    cart = [];
    updateCart();
    note('La cesta quedó vacía.', false);
};


/* =========================================================
   9. EVENTOS DEL CATÁLOGO Y CARRITO
========================================================= */

$('#search').oninput = renderProducts;

$('#cartBtn').onclick = () => {
    updateCart();
    show('cesta');
};

// Un clic en el fondo oscuro (fuera del panel) cierra la cesta.
$('#cesta').onclick = e => {
    if (e.target === e.currentTarget) close('cesta');
};

$('#adminBtn').onclick = async () => {
    if (!isAdmin()) {
        openAuth('El panel de control es solo para el rol administrador.');
        return;
    }
    await refreshAdmin();
    show('adminDialog');
};


/* =========================================================
   10. MÉTODO DE PAGO Y VALIDACIONES DEL CHECKOUT
========================================================= */

const paymentLabels = {
    'tarjeta': 'Tarjeta (simulada)',
    'transferencia': 'Transferencia (simulada)',
    'contra-entrega': 'Contra entrega (simulado)'
};

// Muestra solo los campos del método elegido. El fieldset
// deshabilitado no se valida ni se envía.
function showPaymentFields() {
    const method = $('#paymentMethod').value;
    $('#cardFields').hidden = method !== 'tarjeta';
    $('#cardFields').disabled = method !== 'tarjeta';
    $('#transferInfo').hidden = method !== 'transferencia';
    $('#codInfo').hidden = method !== 'contra-entrega';
}

$('#paymentMethod').onchange = showPaymentFields;

const digitsOnly = value => String(value || '').replace(/\D/g, '');

function cardBrand(number) {
    if (/^4/.test(number)) return 'Visa';
    if (/^3[47]/.test(number)) return 'Amex';
    if (/^(5[1-5]|2(2[2-9]|[3-6]\d|7[01]|720))/.test(number)) return 'Mastercard';
    return '';
}

// Algoritmo de Luhn: el mismo que valida OrdersApi.
function luhn(number) {
    let sum = 0;
    let doubleIt = false;
    for (let i = number.length - 1; i >= 0; i--) {
        let digit = +number[i];
        if (doubleIt) {
            digit *= 2;
            if (digit > 9) digit -= 9;
        }
        sum += digit;
        doubleIt = !doubleIt;
    }
    return sum % 10 === 0;
}

function cardExpired(expiry) {
    const match = /^(0[1-9]|1[0-2])\/(\d{2})$/.exec(expiry);
    if (!match) return true;
    const endOfMonth = new Date(2000 + +match[2], +match[1], 1);
    return endOfMonth <= new Date();
}

// Formatea el número en grupos de 4 mientras se escribe.
$('#checkout [name=cardNumber]').oninput = event => {
    const input = event.target;
    const number = digitsOnly(input.value).slice(0, 19);
    input.value = number.replace(/(\d{4})(?=\d)/g, '$1 ');
    $('#cardBrand').textContent = cardBrand(number);
    input.setCustomValidity('');
};

// Inserta la barra de MM/AA.
$('#checkout [name=cardExpiry]').oninput = event => {
    const input = event.target;
    const digits = digitsOnly(input.value).slice(0, 4);
    input.value = digits.length > 2 ? digits.slice(0, 2) + '/' + digits.slice(2) : digits;
    input.setCustomValidity('');
};

$('#checkout [name=cardCvv]').oninput = event => {
    event.target.value = digitsOnly(event.target.value).slice(0, 4);
    event.target.setCustomValidity('');
};

// Reglas que el HTML no expresa solo: Luhn, vencimiento y CVV según la marca.
function checkCard(form) {
    const number = form.elements.cardNumber;
    const expiry = form.elements.cardExpiry;
    const cvv = form.elements.cardCvv;
    const digits = digitsOnly(number.value);

    number.setCustomValidity(
        digits.length >= 13 && digits.length <= 19 && luhn(digits)
            ? ''
            : 'El número de tarjeta no es válido.'
    );
    expiry.setCustomValidity(
        cardExpired(expiry.value) ? 'La tarjeta está vencida o la fecha no es válida (MM/AA).' : ''
    );

    const cvvLength = cardBrand(digits) === 'Amex' ? 4 : 3;
    cvv.setCustomValidity(
        cvv.value.length === cvvLength ? '' : `El CVV debe tener ${cvvLength} dígitos.`
    );
}


/* =========================================================
   10B. REGISTRO DE PEDIDOS
========================================================= */

$('#checkout').onsubmit = event => event.preventDefault();

function setOrderButton(busy) {
    const button = $('#placeOrder');
    button.disabled = busy;
    button.classList.toggle('is-busy', busy);
    button.setAttribute('aria-busy', busy ? 'true' : 'false');
    button.textContent = busy
        ? 'Creando pedido...'
        : session ? 'Crear pedido' : 'Inicia sesión para comprar';
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

    if (!session) {
        openAuth('Inicia sesión o crea una cuenta para crear el pedido. Tu cesta se conserva.');
        return;
    }

    if ($('#paymentMethod').value === 'tarjeta') {
        checkCard(form);
    }

    if (!form.reportValidity()) {
        message.textContent = 'Revisa los campos marcados. El pedido todavía no se envía.';
        message.className = 'error';
        return;
    }

    const data = new FormData(form);
    setOrderButton(true);
    note('Creando el pedido. Esto puede tardar unos segundos.', false);
    message.textContent = 'Comprobando existencias...';
    message.className = 'notice busy-msg';

    try {
        // Vuelve a pedir el stock: otro cliente pudo reservar mientras tanto.
        const before = JSON.stringify(cart);
        products = await api('/api/inventory/products');
        updateCart();

        if (JSON.stringify(cart) !== before) {
            message.textContent = cart.length
                ? 'El stock cambió y ajustamos la cesta. Revísala y vuelve a pulsar «Crear pedido».'
                : 'Los relojes de tu cesta ya no tienen existencias.';
            message.className = 'error';
            return;
        }

        message.textContent = 'Enviando el pedido y publicando el evento...';

        const paymentMethod = data.get('paymentMethod');
        const order = await post('/api/orders/orders', {
            customer: data.get('customer'),
            phone: data.get('phone'),
            address: data.get('address'),
            paymentMethod,
            card: paymentMethod === 'tarjeta'
                ? {
                    holder: data.get('cardHolder'),
                    number: digitsOnly(data.get('cardNumber')),
                    expiry: data.get('cardExpiry'),
                    cvv: data.get('cardCvv')
                }
                : null,
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
        $('#cardBrand').textContent = '';
        showPaymentFields();
        fillCustomerName();
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
                    ${safe(paymentLabels[o.paymentMethod] || o.paymentMethod)}
                    ${o.paymentDetail ? `· ${safe(o.paymentDetail)}` : ''}
                </p>
                ${o.paymentMethod === 'transferencia' && o.status === 'awaiting-payment'
                    ? `<p class="muted">Referencia de la transferencia: ${safe(String(o.id).slice(0, 8).toUpperCase())}</p>`
                    : ''}

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
    if ($('#adminDialog').open && isAdmin() && !adminTick) {
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
    const box = $('#adminProducts');
    if (!box || box.contains(document.activeElement)) return;

    box.innerHTML = products.length
        ? products.map(p => `
            <div class="admin-row">
                <span>
                    ${safe(p.name)}
                    <br>
                    <small class="${p.reserved > 0 ? 'reserve' : ''}">
                        Stock ${p.stock}, reservado ${p.reserved} · ${money(p.price)}
                    </small>
                </span>
                <span class="ingreso-actions">
                    <input type="number" min="1" max="999" value="1" data-qty="${p.id}"
                        aria-label="Unidades a ingresar de ${safe(p.name)}">
                    <button type="button" class="smallbtn" data-ingreso="${p.id}">Ingresar</button>
                    <button type="button" class="smallbtn" data-adjust="${p.id}" data-change="-1">−1</button>
                </span>
            </div>
        `).join('')
        : '<p class="muted">No hay productos. Usa el formulario para el primer ingreso.</p>';

    box.querySelectorAll('[data-ingreso]').forEach(button => {
        button.onclick = async () => {
            const qty = Number(box.querySelector(`[data-qty="${button.dataset.ingreso}"]`)?.value || 0);
            const message = $('#productMsg');
            if (!Number.isInteger(qty) || qty < 1) {
                message.textContent = 'Indica al menos una unidad para ingresar.';
                message.className = 'error';
                return;
            }

            try {
                await post(`/api/inventory/products/${button.dataset.ingreso}/adjust`, {
                    change: qty,
                    reason: 'Ingreso'
                });
                message.textContent = `Se ingresaron ${qty} unidad(es) al inventario.`;
                message.className = 'success';
                await load();
            } catch (error) {
                message.textContent = error.message;
                message.className = 'error';
            }
        };
    });

    box.querySelectorAll('[data-adjust]').forEach(button => {
        button.onclick = async () => {
            try {
                await post(`/api/inventory/products/${button.dataset.adjust}/adjust`, {
                    change: +button.dataset.change,
                    reason: 'Ajuste manual'
                });
                await load();
            } catch (error) {
                $('#productMsg').textContent = error.message;
                $('#productMsg').className = 'error';
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
                ${order.email ? `<br><small class="muted">${safe(order.email)}</small>` : ''}
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
                    ${order.paymentMethod === 'tarjeta'
                        ? `<option value="tarjeta" selected>Tarjeta ${safe(order.paymentDetail)}</option>`
                        : ''}
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

function renderAdminUsers(users) {
    const box = $('#adminUsers');

    box.innerHTML = users.map(user => `
        <div class="admin-row">
            <span>
                ${safe(user.name)}
                <br>
                <small>${safe(user.email)}</small>
            </span>
            <select class="role-select" data-role="${user.id}" aria-label="Rol de ${safe(user.name)}">
                <option value="cliente" ${user.role === 'cliente' ? 'selected' : ''}>Cliente</option>
                <option value="admin" ${user.role === 'admin' ? 'selected' : ''}>Administrador</option>
            </select>
        </div>
    `).join('') || 'Sin usuarios';

    box.querySelectorAll('[data-role]').forEach(select => {
        select.onchange = async () => {
            try {
                await put(`/api/auth/users/${select.dataset.role}/role`, {
                    role: select.value
                });
            } catch (error) {
                alert(error.message || 'No se pudo cambiar el rol.');
            }
            await refreshAdmin();
        };
    });
}

async function refreshAdmin() {
    await load();

    try {
        const [orders, moves, users] = await Promise.all([
            api('/api/orders/orders'),
            api('/api/inventory/movements'),
            // Si AuthApi falla, el resto del panel sigue funcionando.
            api('/api/auth/users').catch(error => {
                console.error(error);
                return null;
            })
        ]);

        renderReservations(reservationsFromOrders(orders));
        renderAdminOrders(orders);

        // El polling no debe borrar la selección mientras el admin elige un rol.
        if (users && !$('#adminUsers').contains(document.activeElement)) {
            renderAdminUsers(users);
        }

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

$('#newProduct').onsubmit = async event => {
    event.preventDefault();

    if (!isAdmin()) {
        openAuth('El ingreso de productos es solo para el rol administrador.');
        return;
    }

    const form = event.target;
    const data = new FormData(form);
    const message = $('#productMsg');
    message.textContent = 'Guardando en inventario...';
    message.className = 'notice';

    try {
        await post('/api/inventory/products', {
            name: data.get('name'),
            category: data.get('category'),
            price: Number(data.get('price')),
            stock: Number(data.get('stock')),
            description: data.get('description') || '',
            image: '/images/placeholder.svg'
        });

        form.reset();
        form.elements.stock.value = '1';
        message.textContent = 'Producto ingresado. Ya aparece en el catálogo.';
        message.className = 'success';
        await refreshAdmin();
    } catch (error) {
        message.textContent = error.message;
        message.className = 'error';
    }
};


/* =========================================================
   18. SESIÓN: INICIO, REGISTRO Y ROLES
========================================================= */

function fillCustomerName() {
    const input = $('#checkout [name=customer]');
    if (session && !input.value) input.value = session.user.name;
}

// Muestra u oculta lo que depende del rol.
function renderSession() {
    const logged = !!session;

    $('#loginBtn').hidden = logged;
    $('#userBox').hidden = !logged;
    $('#ordersBtn').hidden = !logged;
    $('#adminBtn').hidden = !isAdmin();
    $('#loginHint').hidden = logged;
    $('#userName').textContent = logged
        ? `${session.user.name}${isAdmin() ? ' · admin' : ''}`
        : '';

    if (!$('#placeOrder').classList.contains('is-busy')) setOrderButton(false);
    fillCustomerName();
}

function setSession(value) {
    session = value;
    store.write('te-session', value);

    if (!isAdmin() && $('#adminDialog').open) close('adminDialog');
    if (!value && $('#myOrdersDialog').open) close('myOrdersDialog');

    renderSession();
}

function switchAuthTab(tab) {
    document.querySelectorAll('.auth-tabs .tab').forEach(button => {
        button.classList.toggle('active', button.dataset.tab === tab);
    });
    $('#loginForm').hidden = tab !== 'login';
    $('#registerForm').hidden = tab !== 'register';
    $('#authMsg').textContent = '';
}

document.querySelectorAll('.auth-tabs .tab').forEach(button => {
    button.onclick = () => switchAuthTab(button.dataset.tab);
});

function openAuth(reason = '') {
    $('#authReason').textContent = reason;
    $('#authReason').hidden = !reason;
    switchAuthTab('login');
    if (!$('#authDialog').open) show('authDialog');
}

async function submitAuth(form, url, body) {
    const message = $('#authMsg');
    const button = form.querySelector('button[type=submit]');
    button.disabled = true;
    message.textContent = 'Comprobando...';
    message.className = 'notice';

    try {
        const result = await post(url, body);
        setSession({ token: result.token, user: result.user });
        form.reset();
        close('authDialog');

        if (result.user.role === 'admin') {
            note('Sesión de administrador. El panel de control quedó activo para el ingreso de productos.', false);
            await refreshAdmin();
            show('adminDialog');
            return;
        }

        note(`Hola, ${result.user.name}. Ya puedes crear tu pedido.`, false);
    } catch (error) {
        message.textContent = error.message;
        message.className = 'error';
    } finally {
        button.disabled = false;
    }
}

$('#loginForm').onsubmit = event => {
    event.preventDefault();
    const data = new FormData(event.target);
    submitAuth(event.target, '/api/auth/login', {
        email: data.get('email'),
        password: data.get('password')
    });
};

$('#registerForm').onsubmit = event => {
    event.preventDefault();
    const form = event.target;
    const data = new FormData(form);

    form.elements.confirm.setCustomValidity(
        data.get('password') === data.get('confirm') ? '' : 'Las contraseñas no coinciden.'
    );
    if (!form.reportValidity()) return;

    submitAuth(form, '/api/auth/register', {
        name: data.get('name'),
        email: data.get('email'),
        password: data.get('password')
    });
};

$('#registerForm [name=confirm]').oninput = event => event.target.setCustomValidity('');

$('#loginBtn').onclick = () => openAuth();

$('#logoutBtn').onclick = () => {
    setSession(null);
    note('Cerraste sesión. Tu cesta sigue guardada.', false);
};


/* =========================================================
   19. MIS PEDIDOS
========================================================= */

async function refreshMyOrders() {
    const box = $('#myOrders');

    try {
        const orders = await api('/api/orders/orders/mine');

        box.innerHTML = orders.map(order => `
            <div class="admin-row">
                <span>
                    ${safe(String(order.id).slice(0, 8).toUpperCase())}
                    · ${new Date(order.createdAt).toLocaleDateString('es-SV')}
                    <br>
                    <small>${safe(statuses[order.status] || order.status)}
                        · ${safe(paymentLabels[order.paymentMethod] || order.paymentMethod)}</small>
                </span>
                <span class="order-actions">
                    <b>${money(order.total)}</b>
                    <button type="button" class="smallbtn" data-track="${order.id}">Ver</button>
                </span>
            </div>
        `).join('') || '<p class="muted">Todavía no tienes pedidos.</p>';

        box.querySelectorAll('[data-track]').forEach(button => {
            button.onclick = async () => {
                currentOrder = button.dataset.track;
                close('myOrdersDialog');
                show('orderDialog');
                await refreshOrder();
            };
        });
    } catch (error) {
        box.innerHTML = `<p class="error">${safe(error.message)}</p>`;
    }
}

$('#ordersBtn').onclick = async () => {
    $('#myOrders').textContent = 'Cargando...';
    show('myOrdersDialog');
    await refreshMyOrders();
};


/* =========================================================
   20. INICIALIZACIÓN DE LA APLICACIÓN
========================================================= */

// Comprueba que la sesión guardada siga vigente y actualiza el rol.
async function restoreSession() {
    if (!session?.token) return;

    try {
        const user = await api('/api/auth/me');

        // El token guarda el rol con el que se firmó: si un admin
        // lo cambió, hace falta un token nuevo.
        if (user.role !== session.user.role) {
            setSession(null);
            openAuth('Tu rol cambió. Inicia sesión de nuevo para aplicar los permisos.');
            return;
        }

        setSession({ ...session, user });
    } catch (error) {
        console.error(error);
    }
}

showPaymentFields();
renderSession();
restoreSession();
load();
