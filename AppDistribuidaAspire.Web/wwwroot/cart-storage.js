window.cartStorage = {
    get: () => localStorage.getItem("mercadoclone.cart"),
    set: value => localStorage.setItem("mercadoclone.cart", value)
};
