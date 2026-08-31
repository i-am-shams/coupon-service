Feature: Coupon pricing

  Background:
    Given the menu has the following pizzas:
      | PizzaId | Name       | UnitPrice |
      | 1       | Margherita | 10.00     |
      | 2       | Pepperoni  | 12.00     |

  Scenario: An order with no coupon is charged the full price
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 2        |
    When I price the basket without a coupon
    Then the subtotal should be 20.00
    And the discount should be 0.00
    And the total should be 20.00

  Scenario: A percentage coupon reduces the total correctly
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 3        |
    And a 10% off coupon "PIZZA10" with no minimum spend and a limit of 100 uses, expiring in the future
    When I price the basket with coupon "PIZZA10"
    Then the subtotal should be 30.00
    And the discount should be 3.00
    And the total should be 27.00

  Scenario: A fixed-amount coupon reduces the total correctly
    Given I have a basket with:
      | PizzaId | Quantity |
      | 2       | 2        |
    And a 5.00 fixed-amount coupon "FIVEOFF" with no minimum spend and a limit of 100 uses, expiring in the future
    When I price the basket with coupon "FIVEOFF"
    Then the subtotal should be 24.00
    And the discount should be 5.00
    And the total should be 19.00

  Scenario: A discount never makes the total negative
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 1        |
    And a 50.00 fixed-amount coupon "BIGDEAL" with no minimum spend and a limit of 100 uses, expiring in the future
    When I price the basket with coupon "BIGDEAL"
    Then the subtotal should be 10.00
    And the discount should be 10.00
    And the total should be 0.00

  Scenario: An expired coupon is rejected with the right reason
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 1        |
    And a 10% off coupon "OLDCODE" with no minimum spend and a limit of 100 uses, expiring in the past
    When I price the basket with coupon "OLDCODE"
    Then the coupon should be rejected with reason "Expired"
    And the total should equal the subtotal

  Scenario: A coupon rejected for minimum spend shows the right reason
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 1        |
    And a 10% off coupon "SPEND50" requiring a minimum spend of 50.00 and a limit of 100 uses, expiring in the future
    When I price the basket with coupon "SPEND50"
    Then the coupon should be rejected with reason "MinimumSpendNotMet"
    And the total should equal the subtotal

  Scenario: An unknown coupon code is rejected with the right reason
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 1        |
    When I price the basket with coupon "DOESNOTEXIST"
    Then the coupon should be rejected with reason "NotFound"
    And the total should equal the subtotal

  Scenario: A coupon at its redemption limit is rejected with the right reason
    Given I have a basket with:
      | PizzaId | Quantity |
      | 1       | 1        |
    And a 10% off coupon "MAXED" with no minimum spend, already used 5 times out of a limit of 5, expiring in the future
    When I price the basket with coupon "MAXED"
    Then the coupon should be rejected with reason "RedemptionLimitReached"
    And the total should equal the subtotal

  Scenario: Previewing a coupon three times does not consume a redemption
    Given a coupon "PREVIEW10" with a redemption limit of 1
    When I preview the coupon "PREVIEW10" three times
    Then the coupon should still be valid on the fourth preview

  Scenario: An order is priced from the server's own data, ignoring client claims
    Given I submit an order claiming a pizza costs 1.00 but the server has it at 10.00
    Then the order total should reflect the server price of 10.00

  # Both scenarios below came from probing the deployed gateway, not from reading the
  # code. The first returned 500 for a request that was merely malformed; the second
  # returned 200 and a twenty-billion-euro subtotal.

  Scenario: A request with no items is rejected as a bad request, not a server error
    When I submit a coupon validation with no items property at all
    Then the response status should be 400
    And the response should not be a server error

  Scenario: A basket line quantity above the maximum is rejected
    Given I have a basket with:
      | PizzaId | Quantity      |
      | 1       | 2000000000    |
    When I price the basket without a coupon expecting it to be refused
    Then pricing should be refused because the quantity is out of range

  # The two scenarios below cover the one thing nothing covered before: an order that
  # actually redeems a coupon. Every other scenario either priced a basket without going
  # near the order endpoint, or placed an order carrying no coupon - so rule 4's atomic
  # redemption, the CouponRedemptions audit row and the transaction wrapping them had no
  # test at all, while architecture.md and assumptions.md both stated they did.
  #
  # This is the brief's functional goal at the level the brief states it: the coupon
  # affecting the final order price, not just a priced basket.

  Scenario: An order with a valid coupon is discounted and consumes one redemption
    Given a 10% coupon "ORDER10" with a redemption limit of 5 and a pizza costing 10.00
    When I place an order for 3 of that pizza with coupon "ORDER10"
    Then the response status should be 201
    And the order should report the coupon as applied
    And the order total should be 27.00 with a discount of 3.00
    And the coupon usage count should be 1
    And a redemption should be recorded against that order

  Scenario: An order against an exhausted coupon is created at full price
    Given a 10% coupon "USEDUP" already used 2 times out of 2 and a pizza costing 10.00
    When I place an order for 3 of that pizza with coupon "USEDUP"
    Then the response status should be 201
    And the order should report the coupon as not applied with reason "RedemptionLimitReached"
    And the order total should be 30.00 with a discount of 0.00
    And the coupon usage count should be 2
    And no redemption should be recorded

  # Found by an adversarial review, and it is the same defect class as the missing
  # items array: a schema constraint with no matching request validation. The code
  # evaluated as NotFound, was written onto the order anyway, and met nvarchar(50)
  # inside SaveChangesAsync.
  Scenario: A coupon code longer than the schema allows is rejected as a bad request
    When I submit an order with a coupon code of 60 characters
    Then the response status should be 400
    And the response should not be a server error
