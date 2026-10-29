# Infrastructure of the leasing service (values are never read).
data "azurerm_client_config" "current" {}

resource "azurerm_servicebus_namespace" "bus" {
  name     = "leasing-bus"
  sku      = "Standard"
}

/*
resource "azurerm_old_thing" "ignored" {
}
*/

module "network" {
  source = "./modules/network"
}
