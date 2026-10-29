# eShopOnAbp ground truth (written from source before the first engine run)

- Repository: `https://github.com/abpframework/eShopOnAbp` (archived). Pin: `7bfed7a322d16b34a5a66208c03486fa5dfacee9` (2025-01-08, last commit of `main`).
- `global.json`: SDK `9.0.0`, `rollForward: latestFeature` (resolves to `9.0.320` in the image). ABP packages `9.0.0`.
- C# projects outside `apps/angular`: 88 (`find . -path ./apps/angular -prune -o -name '*.csproj' -print | wc -l`).
- Every item below comes from `grep` over the pinned source (commands in `docs/reports/step-3b-adapters.md`). The engine's output is compared with this list; nothing here was read from engine output.

## Distributed events (ABP event bus, RabbitMQ)

Publishers in application code:

| # | Site | Event type | Event name |
|---|---|---|---|
| P1 | `apps/public-web/src/EShopOnAbp.PublicWeb/EShopOnAbpPublicWebModule.cs:205` | `UserLoggedInEto` (public-web copy) | `EShopOnAbp.Identity.UserLoggedIn` |
| P2 | `services/basket/src/EShopOnAbp.BasketService/Services/BasketProductEventHandler.cs:38` | `BasketProductUpdatedEto` | type name |
| P3 | `services/ordering/src/EShopOnAbp.OrderingService.Domain/PaymentRequests/PaymentRequestEventHandler.cs:35` | `OrderAcceptedEto` | `EShopOnAbp.Order.Accepted` |
| P4 | `services/ordering/src/EShopOnAbp.OrderingService.Domain/Orders/OrderManager.cs:69` | `OrderPlacedEto` | `EShopOnAbp.Order.Placed` |
| P5 | `services/ordering/src/EShopOnAbp.OrderingService.Domain/Orders/OrderManager.cs:106` | `OrderCancelledEto` | `EShopOnAbp.Order.Cancelled` |
| P6 | `services/payment/src/EShopOnAbp.PaymentService.Domain/PaymentRequests/PaymentRequest.cs:46` (`AddDistributedEvent`) | `PaymentRequestCompletedEto` | `EShopOnAbp.Payment.Completed` |
| P7 | `services/payment/src/EShopOnAbp.PaymentService.Domain/PaymentRequests/PaymentRequest.cs:69` (`AddDistributedEvent`) | `PaymentRequestFailedEto` | `EShopOnAbp.Payment.Completed` (same name as P6, in the source) |
| P8 | `services/catalog/src/EShopOnAbp.CatalogService.Domain/Products/Product.cs:114` (`AddDistributedEvent`) | `ProductStockCountChangedEto` | type name |
| P9 | `services/catalog/src/EShopOnAbp.CatalogService.Domain/CatalogServiceDomainModule.cs:22-23` (auto entity events) | `EntityCreated/Updated/DeletedEto<ProductEto>` | generic names around `ProductEto` |

Handlers (`IDistributedEventHandler<T>`, one commented-out handler excluded):

| # | Handler | Event |
|---|---|---|
| H1 | `apps/public-web/.../BasketProductEventHandler.cs:9` | `BasketProductUpdatedEto` |
| H2 | `services/basket/.../Services/BasketProductEventHandler.cs:10` | `EntityUpdatedEto<ProductEto>` |
| H3 | `services/basket/.../Entities/Orders/OrderPlacedEventHandler.cs:8` | `OrderPlacedEto` |
| H4 | `services/identity/.../Handlers/IdentityUserCreationHandler.cs:11` | `EntityCreatedEto<UserEto>` (published by ABP's identity module, a package) |
| H5 | `services/identity/.../UserLoggedInEventHandler.cs:15` | `UserLoggedInEto` (identity copy, same event name as P1) |
| H6 | `services/payment/.../EventHandlers/OrderCancelledEventHandler.cs:11` | `OrderCancelledEto` |
| H7 | `services/catalog/.../OrderHandler.cs:12` | `OrderAcceptedEto` |
| H8 | `services/ordering/.../PaymentRequests/PaymentRequestEventHandler.cs:12` | `PaymentRequestCompletedEto` |
| H9 | `services/administration/.../Handlers/CustomerRoleCreationHandler.cs:11` | `EntityCreatedEto<IdentityRoleEto>` (published by ABP's identity module, a package) |

Expected publish to handler links (by event name, the ABP wire identity): P1-H5, P2-H1, P3-H7, P4-H3, P5-H6, P6-H8, P7-H8 (same name), P9 (Updated)-H2. H4 and H9 have no publisher in this repository; P8 and P9 (Created, Deleted) have no handler.

## HTTP

- Static client proxies: 8 classes deriving `ClientProxyBase<T>`, 32 `RequestAsync` calls in `*.Generated.cs`; 5 proxy descriptions (`*generate-proxy.json`) with 3 + 7 + 9 + 8 + 5 = 32 actions.
- Auto API controllers: `ConventionalControllers.Create` in basket (2 calls), catalog, ordering and cmskit host modules.
- Explicit controllers with `[Route]`: payment `PaymentMethodController`, `PaymentRequestController`; public-web `WidgetsController`.

## gRPC

- Basket calls `ProductPublic.ProductPublicClient.GetByIdAsync` (`BasketProductService.cs:41`), registered with `AddGrpcClient` (`BasketServiceModule.cs:158`); catalog maps `PublicProductGrpService` (`CatalogServiceHttpApiHostModule.cs:115`).
