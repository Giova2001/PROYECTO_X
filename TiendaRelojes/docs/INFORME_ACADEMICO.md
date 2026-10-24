# Informe académico — Actividad Evaluada 2

**Universidad de El Salvador — Ingeniería en Desarrollo de Software / Educación en Línea**

## Título
Desarrollo de una tienda virtual de relojes con gestión de inventario, pedidos y cobros mediante microservicios, ASP.NET Core, Kafka y Docker.

## Planteamiento
En una tienda en línea, el catálogo, los pedidos y el control de existencias deben mantenerse coordinados. Una arquitectura monolítica puede dificultar la separación de responsabilidades y el crecimiento modular. Se propone un prototipo distribuido que comunica cambios mediante eventos.

## Objetivo general
Construir una plataforma demostrativa de venta de relojes con servicios independientes capaces de recibir, procesar y comunicar eventos en tiempo real.

## Objetivos específicos
1. Implementar catálogo e inventario con operaciones básicas.
2. Implementar registro y consulta de pedidos.
3. Simular cobros y cambios de estado.
4. Comunicar servicios mediante Apache Kafka.
5. Ejecutar los componentes con Docker Compose.
6. Documentar decisiones, componentes reutilizados y flujo de ejecución.

## Justificación
El sistema separa inventario, pedidos y cobros en microservicios para facilitar comprensión, mantenimiento y reutilización de contratos de eventos. Kafka permite intercambiar información sin compartir directamente las bases de datos.

## Decisiones de diseño
- ASP.NET Core 8 para APIs por compatibilidad con C#.
- SQLite independiente por servicio para simplificar la demostración.
- Kafka para los eventos de negocio.
- Frontend sin framework para facilitar revisión del código.
- Docker Compose para levantar todos los componentes.
- Cobros simulados por seguridad y alcance académico.

## Reutilización
El proyecto `Shared` centraliza DTOs y mensajería. Cada servicio reutiliza los mismos contratos y se mantiene independiente en la lógica de negocio. Se emplea una arquitectura orientada a eventos comparable con el patrón Observer. El detalle del enfoque, de las decisiones, de la reutilización y de la interacción entre partes está en `docs/ENFOQUE.md`.

## Entregables incluidos
Código fuente, configuración Docker, README, documento de enfoque, arquitectura, manual de usuario, plan de pruebas y este informe.

## Pendientes para entrega final
Completar nombres de integrantes, fecha, capturas reales de funcionamiento, resultados de pruebas, incidencias y enlace al repositorio público GitHub. La exposición debe explicar una venta de principio a fin y la propagación de eventos por Kafka.

## Rúbrica de la actividad
- Implementación: 30 %.
- Calidad del código: 30 %.
- Reutilización y modularidad: 30 %.
- Documentación y presentación: 10 %.
