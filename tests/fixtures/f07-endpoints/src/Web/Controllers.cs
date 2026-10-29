using Microsoft.AspNetCore.Mvc;

namespace Web;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public string Get(int id) => "order";

    [HttpPost]
    public string Create() => "created";
}

public sealed class HomeController : Controller
{
    public string Index() => "home";

    public string About() => "about";
}
