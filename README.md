# Nexo

Tienda de demostración construida con **.NET Aspire**, **Blazor Web App** y **ASP.NET Core Minimal APIs**.
Incluye catálogo, carrito, cuentas de usuario e integración real con **Mercado Pago Checkout Pro**.

> **Importante:** el checkout solo redirige a Mercado Pago si hay un `AccessToken` configurado.
> Las credenciales `TEST-...` fueron discontinuadas por Mercado Pago: la app te deja intentarlas,
> pero muestra una advertencia y es probable que el proveedor las rechace. Los tokens de prueba
> actuales empiezan con `APP_USR-...`. Ver [Configurar Mercado Pago](#configurar-mercado-pago).

## Requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Herramientas de Aspire (se restauran con el SDK)
- Una cuenta de Mercado Pago (para el modo real)

## Ejecutar

```powershell
dotnet run --project AppDistribuidaAspire.AppHost
```

El AppHost levanta el API, la base SQLite y la web. La web se publica por HTTPS en el puerto
`7034` (`https://localhost:7034`). Abrí esa URL y usá el panel de Aspire para ver logs y trazas.

## Configurar Mercado Pago

1. Entrá al [panel de desarrolladores de Mercado Pago](https://www.mercadopago.com.ar/developers/panel/app).
2. Creá una aplicación y abrí **Credenciales de prueba**.
3. Copiá el **Access Token de prueba**. Debe empezar con `APP_USR-`.
4. Configuralo de una de estas formas:

   **Opción A — `appsettings.Development.json`**

   ```json
   "MercadoPago": {
     "AccessToken": "APP_USR-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
     "CheckoutMode": "MercadoPago",
     "UseSandbox": true,
     "AutoApproveCheckout": false
   }
   ```

   **Opción B — User Secrets** (recomendado para no versionar el token)

   ```powershell
   dotnet user-secrets set "MercadoPago:AccessToken" "APP_USR-..." `
     --project AppDistribuidaAspire.ApiService
   ```

   **Opción C — Variable de entorno**

   ```powershell
   $env:MercadoPago__AccessToken = "APP_USR-..."
   ```

5. Reiniciá el AppHost. En el carrito, el botón **Pagar con Mercado Pago** se habilita cuando hay
   un token configurado. Si el token es `TEST-...`, se muestra una advertencia pero el checkout
   igual se puede intentar.

### Credenciales `TEST-` y error 403

La app acepta tokens `TEST-...` para que puedas probar el flujo, pero el carrito muestra una
advertencia porque Mercado Pago discontinuó esas credenciales y es probable que rechace el checkout.

Si ves `403 PA_UNAUTHORIZED_RESULT_FROM_POLICIES` o el pago falla con el token `TEST-`:

- Generá un token nuevo `APP_USR-...` en el panel de desarrolladores.
- Si el token es nuevo y sigue fallando, revisá que la aplicación no esté bloqueada o que las
  credenciales no hayan sido revocadas.

### URLs de retorno

La web envía su propia `Navigation.BaseUri` al API, así que los `back_urls` vuelven al puerto HTTPS
real donde está corriendo la tienda. En desarrollo se acepta `https://localhost:...`; `auto_return`
solo se envía cuando la URL de retorno es pública (Mercado Pago lo exige).

### Webhook (opcional)

Para conciliar pagos automáticamente:

```json
"NotificationUrl": "https://tu-dominio-publico/api/payments/webhook",
"WebhookSecret": "el-secreto-de-tu-webhook"
```

La URL debe ser HTTPS pública (no `localhost`) y el webhook valida la firma `x-signature`.

## Modos de checkout

| `CheckoutMode` | Comportamiento |
| --- | --- |
| `MercadoPago` | Crea una preferencia real y redirige al checkout de Mercado Pago. |
| `Demo` | Aprueba la compra al instante, sin credenciales ni cobro. Útil para probar la UI. |

`AutoApproveCheckout` marca la orden como aprobada al iniciar el checkout (solo para pruebas).
Si Mercado Pago falla y `AutoApproveCheckout` está activo, el API cae a un checkout demo; si está
desactivado, devuelve el error real para que puedas corregir la configuración.

## Estructura

```
AppDistribuidaAspire.AppHost/          Orquestación de Aspire
AppDistribuidaAspire.ApiService/       Minimal APIs, EF Core + SQLite, Mercado Pago
  Payments/                            Checkout, verificación y endpoints de pago
AppDistribuidaAspire.Web/              Blazor Web App (Interactive Server)
  Components/Pages/                    Home, carrito, auth, compras
  Components/Shared/                   Marca, tarjetas, verificación de pago
  Services/                            Clientes HTTP y estado del carrito
AppDistribuidaAspire.ApiService.Tests/ Tests del servicio de checkout
```

## Tests

```powershell
dotnet test AppDistribuidaAspire.ApiService.Tests/AppDistribuidaAspire.ApiService.Tests.csproj
```

## Notas de diseño

- Marca **Nexo**, paleta índigo/violeta con acento ámbar y tarjetas redondeadas.
- Los totales y el stock se validan siempre en el servidor.
- El resultado del pago se confirma consultando el API, nunca solo con los parámetros del navegador.
- Las órdenes pendientes expiran a los 5 minutos sin pago.
