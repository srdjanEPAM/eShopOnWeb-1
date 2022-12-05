using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    private readonly string ConnectionString = "Endpoint=sb://sb-ewbshop-namespace.servicebus.windows.net/;SharedAccessKeyName=all;SharedAccessKey=NCizQVszeHTPFVEqra9t63/M3Q5j+Ho8G0VdgFKPzQY=;EntityPath=orders";
    private readonly string QueueName = "orders";
    private readonly string DeliveryOrderURL = "https://fa-ewbshop-02.azurewebsites.net/api/DeliveryOrderProcessor?code=CWcnOBj28IoATZ26qyxSGKtqi4HmcCs1M6vbKkVWDylaAzFuR4uoVQ==";

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        await ProcessOrder(order);
    }

    private async Task ProcessOrder(Order order)
    {
        foreach(var item in order.OrderItems)
        {
            var orderPayload = new OrderDetails
            {
                ItemId = item?.Id ?? 0,
                Quantity = item?.Units ?? 0,
            };

            await ReserveOrderItems(orderPayload);
            await DeliverOrderItems(orderPayload);
        }
    }

    private async Task ReserveOrderItems(OrderDetails orderPayload)
    {
        await using (ServiceBusClient client = new ServiceBusClient(ConnectionString))
        {
            ServiceBusSender sender = client.CreateSender(QueueName);

            string jsonEntity = JsonConvert.SerializeObject(orderPayload);
            ServiceBusMessage serializedContents = new ServiceBusMessage(jsonEntity);
            await sender.SendMessageAsync(serializedContents);
        }
    }
    private async Task DeliverOrderItems(OrderDetails orderPayload)
    {
        var httpClient = new HttpClient();
        var stringPayload = JsonConvert.SerializeObject(orderPayload);
        var httpContent = new StringContent(stringPayload, Encoding.UTF8, "application/json");
        var httpResponse = await httpClient.PostAsync(DeliveryOrderURL, httpContent);
        if (httpResponse.Content != null)
        {
            var responseContent = await httpResponse.Content.ReadAsStringAsync();
        }
    }

    private class OrderDetails
    {
        public int ItemId { get; set; }
        public int Quantity { get; set; }
    }
}
