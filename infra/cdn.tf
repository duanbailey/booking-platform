# HTTPS without a domain (T27b, decided 2026-11-24): CloudFront's default *.cloudfront.net certificate, in front of
# the internal load balancer through a VPC origin, so the load balancer has no public address and the CloudFront to
# load balancer hop stays inside AWS. Caching is off and every viewer header except Host is forwarded (an API, not a
# website); HTTP is redirected to HTTPS here, which is why the API's own redirect is off (Https__Redirect=false).

resource "aws_cloudfront_vpc_origin" "api" {
  vpc_origin_endpoint_config {
    name                   = "${local.name}-api"
    arn                    = aws_lb.api.arn
    http_port              = 80
    https_port             = 443
    origin_protocol_policy = "http-only"
    origin_ssl_protocols {
      items    = ["TLSv1.2"]
      quantity = 1
    }
  }
}

data "aws_cloudfront_cache_policy" "disabled" {
  name = "Managed-CachingDisabled"
}

data "aws_cloudfront_origin_request_policy" "all_viewer_except_host" {
  name = "Managed-AllViewerExceptHostHeader"
}

resource "aws_cloudfront_distribution" "api" {
  enabled         = true
  comment         = "${local.name} API"
  price_class     = "PriceClass_100"
  is_ipv6_enabled = true
  http_version    = "http2and3"

  origin {
    origin_id   = "api"
    domain_name = aws_lb.api.dns_name
    vpc_origin_config {
      vpc_origin_id = aws_cloudfront_vpc_origin.api.id
    }
  }

  default_cache_behavior {
    target_origin_id         = "api"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_viewer_except_host.id
    compress                 = true
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

locals {
  api_url      = "https://${aws_cloudfront_distribution.api.domain_name}"
  app_base_url = var.app_base_url != "" ? var.app_base_url : local.api_url
}
