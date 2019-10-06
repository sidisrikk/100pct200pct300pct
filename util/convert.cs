int priceDistanceToPip(double priceDistance){
    return Math.Round(Math.Pow(10, Symbol.Digits - 1) * Math.Abs(priceDistance));
}